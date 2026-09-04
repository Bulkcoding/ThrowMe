using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using ThrowMe.Models;

namespace ThrowMe.Services;

/// <summary>
/// Claude Code 훅이 보내는 이벤트를 로컬 HTTP 로 받아 세션별 상태로 모은다.
///
/// 훅 명령은 <c>curl.exe ... POST http://127.0.0.1:포트/state?event=이벤트</c> 로, Claude Code 가
/// 훅에 넘겨 주는 JSON(stdin)을 본문으로 그대로 보낸다. 응답은 항상 204(본문 없음)여야 한다 —
/// 일부 훅(UserPromptSubmit 등)은 명령의 stdout 을 대화 맥락에 넣기 때문이다.
///
/// 보여 줄 상태는 <b>가장 최근에 이벤트를 보낸 세션</b>의 것이다. 여러 세션이 있을 때 우선순위 최댓값을
/// 쓰면, 다른 창(예: IDE 안에서 도는 에이전트)이 계속 도구를 쓰는 동안 내가 보고 있는 CLI 의 완료가
/// 묻혀 버린다. 단, 기다림·오류는 어느 세션에서 나든 잠깐(<see cref="AttentionHold"/>) 우선한다.
/// Stop 은 <see cref="AgentState.Done"/> 으로 들어와 <see cref="DoneHold"/> 뒤 저절로 Idle 이 된다.
/// 10분 동안 아무 이벤트가 없는 세션은 잊는다(비정상 종료 대비).
/// </summary>
public sealed class CliStateServer
{
    private sealed class Session
    {
        public AgentState State;
        public int Subagents;
        public DateTime LastSeen;
        public DateTime StateSince;
        public string Cwd = "";
        public long Hwnd;   // 세션이 도는 터미널 창 핸들(0 = 모름). --hook 이벤트가 채운다.
    }

    /// <summary>세션 표시용 스냅샷.</summary>
    public sealed record SessionInfo(string Id, AgentState State, string Cwd, DateTime LastSeen, long Hwnd);

    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(10);
    /// <summary>턴 완료(Done) 표시를 유지하는 시간. 지나면 Idle.</summary>
    private static readonly TimeSpan DoneHold = TimeSpan.FromSeconds(5);
    /// <summary>다른 세션의 기다림·오류가 최근 세션보다 우선하는 시간.</summary>
    private static readonly TimeSpan AttentionHold = TimeSpan.FromSeconds(90);

    private readonly int _port;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private HttpListener? _listener;
    private System.Threading.Timer? _tick;
    private AgentState _last = AgentState.Idle;

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }
    public int Port => _port;

    /// <summary>합친 상태가 바뀌었다(백그라운드 스레드에서 발생 — UI 는 Dispatcher 로 넘길 것).</summary>
    public event EventHandler<AgentState>? StateChanged;
    /// <summary>세션 수 등 표시용 정보가 바뀌었다.</summary>
    public event EventHandler? SessionsChanged;

    public CliStateServer(int port) => _port = port;

    public AgentState Current => _last;

    public int SessionCount
    {
        get
        {
            var cutoff = DateTime.UtcNow - SessionTtl;
            return _sessions.Values.Count(s => s.LastSeen >= cutoff);
        }
    }

    /// <summary>살아 있는 세션 목록(최근 활동순).</summary>
    public IReadOnlyList<SessionInfo> Sessions
    {
        get
        {
            var cutoff = DateTime.UtcNow - SessionTtl;
            return _sessions
                .Where(kv => kv.Value.LastSeen >= cutoff)
                .OrderByDescending(kv => kv.Value.LastSeen)
                .Select(kv => new SessionInfo(kv.Key, kv.Value.State, kv.Value.Cwd, kv.Value.LastSeen, kv.Value.Hwnd))
                .ToList();
        }
    }

    public void Start()
    {
        if (IsRunning) return;
        try
        {
            var l = new HttpListener();
            l.Prefixes.Add($"http://127.0.0.1:{_port}/");
            l.Start();
            _listener = l;
            IsRunning = true;
            LastError = null;
            _ = Task.Run(() => Loop(l));
            // Done → Idle 내려가기, 오래된 세션 정리는 이벤트가 없어도 일어나야 하므로 주기적으로 다시 계산한다.
            _tick = new System.Threading.Timer(_ => { try { Recompute(); } catch { } }, null, 1000, 1000);
            Logger.Info($"CLI state server listening on 127.0.0.1:{_port}.");
        }
        catch (Exception ex)
        {
            IsRunning = false;
            LastError = ex.Message;
            Logger.Error($"CLI state server failed to start on port {_port}.", ex);
        }
    }

    public void Stop()
    {
        var l = _listener;
        _listener = null;
        IsRunning = false;
        try { _tick?.Dispose(); } catch { }
        _tick = null;
        try { l?.Stop(); l?.Close(); } catch { }
        _sessions.Clear();
        SetAggregate(AgentState.Idle);
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task Loop(HttpListener l)
    {
        while (l.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await l.GetContextAsync().ConfigureAwait(false); }
            catch { break; } // Stop() 으로 닫힘
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            if (req.HttpMethod == "POST" && req.Url?.AbsolutePath == "/state")
            {
                string body;
                using (var sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8))
                    body = sr.ReadToEnd();
                string ev = req.QueryString["event"] ?? "";
                long.TryParse(req.QueryString["hwnd"], out long hwnd);
                Apply(ev, body, hwnd);
                ctx.Response.StatusCode = 204;
            }
            else if (req.HttpMethod == "GET" && req.Url?.AbsolutePath == "/health")
            {
                ctx.Response.StatusCode = 200;
                var sessions = string.Join(",", Sessions.Select(s => $"{{\"id\":\"{s.Id}\",\"state\":\"{s.State}\",\"cwd\":{JsonSerializer.Serialize(s.Cwd)}}}"));
                byte[] b = Encoding.UTF8.GetBytes($"{{\"app\":\"ThrowMe\",\"state\":\"{_last}\",\"sessions\":[{sessions}]}}");
                ctx.Response.ContentType = "application/json";
                ctx.Response.OutputStream.Write(b, 0, b.Length);
            }
            else ctx.Response.StatusCode = 404;
        }
        catch (Exception ex)
        {
            Logger.Error("CLI state request failed.", ex);
            try { ctx.Response.StatusCode = 500; } catch { }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    private void Apply(string ev, string body, long hwnd = 0)
    {
        string sid = "default";
        string toolName = "", notificationType = "", cwd = "";
        try
        {
            if (!string.IsNullOrWhiteSpace(body))
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (string.IsNullOrEmpty(ev) && root.TryGetProperty("hook_event_name", out var he) && he.ValueKind == JsonValueKind.String)
                        ev = he.GetString() ?? "";
                    if (root.TryGetProperty("session_id", out var se) && se.ValueKind == JsonValueKind.String)
                        sid = se.GetString() ?? sid;
                    if (root.TryGetProperty("tool_name", out var te) && te.ValueKind == JsonValueKind.String)
                        toolName = te.GetString() ?? "";
                    if (root.TryGetProperty("notification_type", out var ne) && ne.ValueKind == JsonValueKind.String)
                        notificationType = ne.GetString() ?? "";
                    if (root.TryGetProperty("cwd", out var ce) && ce.ValueKind == JsonValueKind.String)
                        cwd = ce.GetString() ?? "";
                }
            }
        }
        catch { /* 본문이 JSON 이 아니어도 이벤트 이름만으로 처리한다 */ }

        if (string.IsNullOrEmpty(ev)) return;
        var now = DateTime.UtcNow;

        if (ev == "SessionEnd")
        {
            _sessions.TryRemove(sid, out _);
        }
        else
        {
            var s = _sessions.GetOrAdd(sid, _ => new Session { StateSince = now });
            s.LastSeen = now;
            if (cwd.Length > 0) s.Cwd = cwd;
            if (hwnd != 0) s.Hwnd = hwnd;   // --hook 이 붙여 준 터미널 창 핸들(세션당 한 번 잡히면 유지)
            var before = s.State;
            switch (ev)
            {
                case "SessionStart": s.State = AgentState.Idle; s.Subagents = 0; break;
                case "UserPromptSubmit": s.State = AgentState.Thinking; break;
                case "PreToolUse":
                    // 서브에이전트(Task 도구) 실행은 저글링으로 본다.
                    if (toolName == "Task" || toolName == "Agent") s.Subagents = Math.Max(s.Subagents, 1);
                    s.State = s.Subagents > 0 ? AgentState.Juggling : AgentState.Working;
                    break;
                case "PostToolUse": s.State = s.Subagents > 0 ? AgentState.Juggling : AgentState.Working; break;
                case "PostToolUseFailure": s.State = AgentState.Error; break;
                case "SubagentStart": s.Subagents++; s.State = AgentState.Juggling; break;
                case "SubagentStop":
                    s.Subagents = Math.Max(0, s.Subagents - 1);
                    s.State = s.Subagents > 0 ? AgentState.Juggling : AgentState.Working;
                    break;
                case "Stop": s.State = AgentState.Done; s.Subagents = 0; break;
                case "StopFailure": s.State = AgentState.Error; s.Subagents = 0; break;
                case "PermissionRequest":
                case "Elicitation": s.State = AgentState.Waiting; break;
                case "Notification":
                    if (notificationType is "permission_prompt" or "elicitation_dialog" or "elicitation") s.State = AgentState.Waiting;
                    else if (notificationType == "idle_prompt") s.State = AgentState.Idle;
                    break;
                case "PreCompact":
                case "PostCompact": s.State = AgentState.Thinking; break;
                default: break; // 모르는 이벤트는 마지막 접촉 시각만 갱신
            }
            if (s.State != before) s.StateSince = now;
        }

        Recompute();
        Logger.Info($"CLI event {ev} sid={Short(sid)} tool={toolName} -> shown={_last}, sessions={SessionCount}");
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Short(string sid) => sid.Length > 8 ? sid[..8] : sid;

    /// <summary>
    /// 보여 줄 상태를 다시 고른다. 오래된 세션 정리, Done 만료도 여기서 한다.
    /// 기본은 최근 활동 세션의 상태. 최근 <see cref="AttentionHold"/> 안에 기다림·오류가 된 세션이 있으면 그것이 이긴다.
    /// </summary>
    private void Recompute()
    {
        var now = DateTime.UtcNow;
        var cutoff = now - SessionTtl;
        Session? latest = null;
        AgentState attention = AgentState.Idle;
        bool changedSessions = false;

        foreach (var (key, s) in _sessions)
        {
            if (s.LastSeen < cutoff) { _sessions.TryRemove(key, out _); changedSessions = true; continue; }
            if (s.State == AgentState.Done && now - s.StateSince >= DoneHold)
            {
                s.State = AgentState.Idle;
                s.StateSince = now;
                changedSessions = true;
            }
            if (latest == null || s.LastSeen > latest.LastSeen) latest = s;
            if (s.State >= AgentState.Waiting && now - s.StateSince < AttentionHold && s.State > attention)
                attention = s.State;
        }

        var shown = attention != AgentState.Idle ? attention : (latest?.State ?? AgentState.Idle);
        SetAggregate(shown);
        if (changedSessions) SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetAggregate(AgentState s)
    {
        if (s == _last) return;
        _last = s;
        StateChanged?.Invoke(this, s);
    }
}
