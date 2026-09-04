using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ThrowMe.Services;

/// <summary>
/// 사용자 Claude Code 설정(~/.claude/settings.json)에 상태 전송 훅을 넣고 뺀다.
///
/// 훅 명령은 Windows 에 기본으로 있는 curl.exe 만 쓴다(node 같은 별도 런타임 불필요).
/// Claude Code 가 stdin 으로 주는 이벤트 JSON 을 그대로 본문으로 보내고, 이벤트 이름은 쿼리로 붙인다.
/// 우리 항목은 명령 안의 "127.0.0.1:포트/state" 문자열로 알아본다 — 다른 도구(clawd-on-desk 등)의 훅은 건드리지 않는다.
/// </summary>
public static class ClaudeHooksInstaller
{
    public static readonly string[] Events =
    {
        "SessionStart", "SessionEnd", "UserPromptSubmit",
        "PreToolUse", "PostToolUse", "PostToolUseFailure",
        "Notification", "PermissionRequest", "Elicitation",
        "Stop", "StopFailure", "SubagentStart", "SubagentStop",
        "PreCompact", "PostCompact",
    };

    public static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    private static string Marker(int port) => $"127.0.0.1:{port}/state";

    /// <summary>창 핸들까지 붙여 보내야 하는(드문) 이벤트 — 세션과 터미널 창을 잇는 데 쓴다.</summary>
    private static readonly HashSet<string> WindowEvents = new(StringComparer.Ordinal) { "SessionStart", "UserPromptSubmit" };

    public static string CommandFor(string ev, int port)
    {
        // 세션 시작·프롬프트 제출만 우리 exe(--hook)로 받아 터미널 창 핸들을 함께 보낸다.
        // 창은 세션당 한 번 잡으면 되므로 빈번한 상태 이벤트는 가벼운 curl 로 그대로 둔다.
        if (WindowEvents.Contains(ev))
        {
            string exe = Process.GetCurrentProcess().MainModule?.FileName ?? "";
            if (exe.Length > 0)
                return $"\"{exe}\" --hook {ev} {port}";
        }
        return $"curl.exe -s -m 2 -X POST \"http://127.0.0.1:{port}/state?event={ev}\" -H \"Content-Type: application/json\" --data-binary @-";
    }

    /// <summary>우리 훅이 하나라도 들어 있는가.</summary>
    public static bool IsInstalled(int port)
    {
        try
        {
            if (!File.Exists(SettingsPath)) return false;
            var root = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject;
            if (root?["hooks"] is not JsonObject hooks) return false;
            foreach (var (_, arr) in hooks)
                if (arr is JsonArray a && a.Any(e => IsOurs(e, port))) return true;
            return false;
        }
        catch { return false; }
    }

    public static bool Install(int port, out string error)
    {
        error = "";
        try
        {
            var root = LoadOrNew(out string? loadError);
            if (root == null) { error = loadError ?? "설정 파일을 읽지 못했습니다."; return false; }
            var hooks = root["hooks"] as JsonObject;
            if (hooks == null) root["hooks"] = hooks = new JsonObject();

            foreach (string ev in Events)
            {
                var arr = hooks[ev] as JsonArray;
                if (arr == null) hooks[ev] = arr = new JsonArray();
                RemoveOurs(arr, port);
                arr.Add(new JsonObject
                {
                    ["matcher"] = "",
                    ["hooks"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = CommandFor(ev, port),
                        ["timeout"] = 5,
                        ["async"] = true,
                    }),
                });
            }
            Save(root);
            Logger.Info($"Claude Code hooks installed ({Events.Length} events, port {port}).");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Claude Code hooks install failed.", ex);
            error = ex.Message;
            return false;
        }
    }

    public static bool Uninstall(int port, out string error)
    {
        error = "";
        try
        {
            if (!File.Exists(SettingsPath)) return true;
            var root = LoadOrNew(out string? loadError);
            if (root == null) { error = loadError ?? "설정 파일을 읽지 못했습니다."; return false; }
            if (root["hooks"] is JsonObject hooks)
            {
                foreach (string ev in hooks.Select(kv => kv.Key).ToList())
                {
                    if (hooks[ev] is JsonArray arr)
                    {
                        RemoveOurs(arr, port);
                        if (arr.Count == 0) hooks.Remove(ev);
                    }
                }
                if (hooks.Count == 0) root.Remove("hooks");
            }
            Save(root);
            Logger.Info("Claude Code hooks removed.");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("Claude Code hooks uninstall failed.", ex);
            error = ex.Message;
            return false;
        }
    }

    private static JsonObject? LoadOrNew(out string? error)
    {
        error = null;
        if (!File.Exists(SettingsPath)) return new JsonObject();
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(SettingsPath), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (node is JsonObject o) return o;
            error = "settings.json 최상위가 객체가 아닙니다.";
            return null;
        }
        catch (Exception ex)
        {
            error = "settings.json 을 해석하지 못했습니다: " + ex.Message;
            return null;
        }
    }

    private static void Save(JsonObject root)
    {
        string dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        // 한 번 백업해 두면 잘못됐을 때 되돌릴 수 있다(덮어쓰지 않고 최초 1회만).
        string bak = SettingsPath + ".throwme.bak";
        if (File.Exists(SettingsPath) && !File.Exists(bak)) File.Copy(SettingsPath, bak);

        string tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, SettingsPath, overwrite: true);
    }

    private static bool IsOurs(JsonNode? entry, int port)
    {
        if (entry is not JsonObject o || o["hooks"] is not JsonArray inner) return false;
        foreach (var h in inner)
            if (h is JsonObject ho && ho["command"]?.GetValue<string>() is string cmd && cmd.Contains(Marker(port), StringComparison.Ordinal))
                return true;
        return false;
    }

    private static void RemoveOurs(JsonArray arr, int port)
    {
        for (int i = arr.Count - 1; i >= 0; i--)
            if (IsOurs(arr[i], port)) arr.RemoveAt(i);
    }
}
