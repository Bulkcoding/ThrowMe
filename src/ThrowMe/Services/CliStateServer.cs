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
/// 세션은 session_id 로 구분하고, 여러 세션이 있으면 우선순위가 높은 상태(<see cref="AgentState"/>)를 보여 준다.
/// 10분 동안 아무 이벤트가 없는 세션은 잊는다(비정상 종료 대비).
/// </summary>
public sealed class CliStateServer
{
    private sealed class Session
    {
        public AgentState State;
        public int Subagents;
        public DateTime LastSeen;
    }

    private static readonly TimeSpan SessionTtl = TimeSpan.FromMinutes(10);

    private readonly int _port;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private HttpListener? _listener;
    private AgentState _last = AgentState.Idle;

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }
    public int Port => _port;

    /// <summary>합친 상태가 바뀌었다(백그라운드 스레드에서 발생 — UI 는 Dispatcher 로 넘길 것).</summary>
    public event EventHandler<AgentState>? StateChanged;
    /// <summary>어느 세션이 한 턴을 끝냈다(Stop). 손 흔들기 같은 일회성 반응용.</summary>
    public event EventHandler? TurnFinished;
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
                Apply(ev, body);
                ctx.Response.StatusCode = 204;
            }
            else if (req.HttpMethod == "GET" && req.Url?.AbsolutePath == "/health")
            {
                ctx.Response.StatusCode = 200;
                byte[] b = Encoding.UTF8.GetBytes("{\"app\":\"ThrowMe\",\"state\":\"" + _last + "\"}");
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

    private void Apply(string ev, string body)
    {
        string sid = "default";
        string toolName = "", notificationType = "";
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
                }
            }
        }
        catch { /* 본문이 JSON 이 아니어도 이벤트 이름만으로 처리한다 */ }

        if (string.IsNullOrEmpty(ev)) return;
        bool turnDone = false;

        if (ev == "SessionEnd")
        {
            _sessions.TryRemove(sid, out _);
        }
        else
        {
            var s = _sessions.GetOrAdd(sid, _ => new Session());
            s.LastSeen = DateTime.UtcNow;
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
                case "Stop": s.State = AgentState.Idle; s.Subagents = 0; turnDone = true; break;
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
        }

        Recompute();
        if (turnDone) TurnFinished?.Invoke(this, EventArgs.Empty);
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Recompute()
    {
        var cutoff = DateTime.UtcNow - SessionTtl;
        var agg = AgentState.Idle;
        foreach (var (key, s) in _sessions)
        {
            if (s.LastSeen < cutoff) { _sessions.TryRemove(key, out _); continue; }
            if (s.State > agg) agg = s.State;
        }
        SetAggregate(agg);
    }

    private void SetAggregate(AgentState s)
    {
        if (s == _last) return;
        _last = s;
        StateChanged?.Invoke(this, s);
    }
}
