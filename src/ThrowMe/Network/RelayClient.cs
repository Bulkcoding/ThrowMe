using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ThrowMe.Services;

namespace ThrowMe.Network;

/// <summary>연결 상태(설정창·디버그 표시용).</summary>
public enum RelayState { Disabled, Connecting, Connected, Reconnecting, Failed }

/// <summary>
/// 릴레이 서버와의 WSS 상시 연결을 관리한다.
/// - 아웃바운드 WSS 라 NAT/방화벽 무관.
/// - 끊기면 지수 백오프로 자동 재연결 → 재HELLO.
/// - 주기적 HEARTBEAT 로 죽은 연결 감지.
///
/// UI 스레드 비의존: 콜백(<see cref="MessageReceived"/> 등)은 백그라운드 스레드에서 호출되므로
/// 구독자(SlimeWindow)가 Dispatcher 로 마샬링한다.
/// </summary>
public sealed class RelayClient : IDisposable
{
    private readonly AuthService _auth;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    private CancellationTokenSource? _cts;
    private ClientWebSocket? _ws;
    private Task? _loop;

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    public RelayState State { get; private set; } = RelayState.Disabled;

    /// <summary>서버로부터 봉투 1건 수신(백그라운드 스레드).</summary>
    public event Action<Envelope>? MessageReceived;
    /// <summary>HELLO 성공 여부와 무관하게 WSS 물리 연결 수립됨.</summary>
    public event Action? Connected;
    /// <summary>연결 끊김(재연결 루프가 다시 시도함).</summary>
    public event Action? Disconnected;
    /// <summary>상태 변화 통지.</summary>
    public event Action<RelayState>? StateChanged;

    public RelayClient(AuthService auth) => _auth = auth;

    /// <summary>연결 루프 시작(설정돼 있을 때만). 이미 돌고 있으면 무시.</summary>
    public void Start()
    {
        if (_loop is { IsCompleted: false }) return;
        if (!_auth.IsConfigured)
        {
            SetState(RelayState.Disabled);
            return;
        }
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    /// <summary>연결 종료(설정 끄기/앱 종료).</summary>
    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { if (_loop != null) await _loop.ConfigureAwait(false); }
        catch { /* ignore */ }
        SetState(RelayState.Disabled);
    }

    /// <summary>봉투 전송(연결 없으면 조용히 무시 — 로컬 토이는 계속 동작).</summary>
    public async Task SendAsync(Envelope env)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open }) return;
        string json = RelayJson.Serialize(env);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.Error("Relay send failed.", ex);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ── 연결 루프 ─────────────────────────────────────────────
    private async Task RunAsync(CancellationToken ct)
    {
        TimeSpan backoff = MinBackoff;
        bool firstAttempt = true;

        while (!ct.IsCancellationRequested)
        {
            SetState(firstAttempt ? RelayState.Connecting : RelayState.Reconnecting);
            firstAttempt = false;
            var ws = new ClientWebSocket();
            _ws = ws;
            try
            {
                await ws.ConnectAsync(_auth.BuildUri(), ct).ConfigureAwait(false);
                await SendRawAsync(ws, _auth.BuildHello(), ct).ConfigureAwait(false);
                SetState(RelayState.Connected);
                Connected?.Invoke();
                backoff = MinBackoff; // 성공 시 백오프 리셋

                using var hbCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task hb = HeartbeatAsync(ws, hbCts.Token);
                await ReceiveLoopAsync(ws, ct).ConfigureAwait(false);
                hbCts.Cancel();
                try { await hb.ConfigureAwait(false); } catch { /* ignore */ }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Error("Relay connection error.", ex);
            }
            finally
            {
                Disconnected?.Invoke();
                try { ws.Dispose(); } catch { /* ignore */ }
                if (ReferenceEquals(_ws, ws)) _ws = null;
            }

            if (ct.IsCancellationRequested) break;
            SetState(RelayState.Reconnecting);
            try { await Task.Delay(backoff, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            backoff = TimeSpan.FromMilliseconds(Math.Min(MaxBackoff.TotalMilliseconds, backoff.TotalMilliseconds * 2));
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            ms.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None)
                        .ConfigureAwait(false);
                    return;
                }
                ms.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (ms.Length == 0) continue;
            string json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
            var env = RelayJson.Deserialize(json);
            if (env != null)
            {
                try { MessageReceived?.Invoke(env); }
                catch (Exception ex) { Logger.Error("Relay message handler threw.", ex); }
            }
        }
    }

    private async Task HeartbeatAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(HeartbeatInterval, ct).ConfigureAwait(false);
                await SendRawAsync(ws, new Envelope { Type = MsgType.Heartbeat }, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* normal on shutdown */ }
        catch (Exception ex) { Logger.Error("Relay heartbeat failed.", ex); }
    }

    private async Task SendRawAsync(ClientWebSocket ws, Envelope env, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(RelayJson.Serialize(env));
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void SetState(RelayState s)
    {
        if (State == s) return;
        State = s;
        try { StateChanged?.Invoke(s); } catch { /* ignore */ }
    }

    /// <summary>
    /// 방에서 정상적으로 나간다(앱 종료 시). WebSocket 종료 프레임을 보내면 서버가 즉시
    /// 이탈로 처리해 남은 사람들의 파티 목록에서 바로 사라지고, 공 소유권도 곧장 넘어간다.
    /// 그냥 프로세스를 끝내면 서버는 소켓이 끊긴 걸 뒤늦게 알아채므로 한동안 유령으로 남는다.
    ///
    /// 종료를 지연시키면 안 되므로 짧게 기다리고 포기한다.
    /// </summary>
    public async Task LeaveRoomAsync(int timeoutMs = 1200)
    {
        var ws = _ws;
        if (ws is not { State: WebSocketState.Open }) return;

        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "leaving", cts.Token)
                    .ConfigureAwait(false);
            Logger.Info("Left room (websocket closed normally).");
        }
        catch (Exception ex)
        {
            // 네트워크가 이미 끊겼을 수 있다. 종료를 막지 않는다.
            Logger.Info($"Graceful leave skipped: {ex.GetType().Name}");
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        try { _ws?.Dispose(); } catch { /* ignore */ }
        _sendLock.Dispose();
        _cts?.Dispose();
    }
}
