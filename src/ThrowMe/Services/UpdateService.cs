using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace ThrowMe.Services;

/// <summary>
/// GitHub 릴리스 기반 자동 업데이트(조용히 자동 적용).
///
/// 흐름:
///  1) 앱 실행 중 백그라운드로 최신 릴리스를 확인해, 더 높은 버전이면 exe 를 staging 폴더에 받아 둔다.
///  2) 다음 실행의 맨 처음(창 생성 전)에 staging 된 최신 exe 가 있으면, 현재 프로세스 종료를 기다렸다
///     원본 exe 를 교체하고 재실행하는 스크립트를 띄운 뒤 스스로 종료한다.
///
/// 실행 중인 exe 는 덮어쓸 수 없으므로, 교체는 항상 "다음 실행 시작 시점"에 수행한다.
/// </summary>
public static class UpdateService
{
    private static string StageDir => Path.Combine(AppPaths.Local, "update");
    private static string PendingExe => Path.Combine(StageDir, "ThrowMe.pending.exe");
    private static string PendingVer => Path.Combine(StageDir, "pending.txt");

    /// <summary>받아 둔 버전의 릴리스 노트(교체 전). apply.cmd 는 이 파일을 지우지 않는다.</summary>
    private static string PendingNotes => Path.Combine(StageDir, "pending_notes.json");

    /// <summary>교체가 실제로 일어난 뒤, 새 버전이 처음 뜰 때 보여줄 노트.</summary>
    private static string AppliedNotes => Path.Combine(StageDir, "applied_notes.json");
    private static string TokenFile => Path.Combine(AppPaths.Local, "update_token.txt");

    /// <summary>현재 실행 파일 버전(4자리 정규화).</summary>
    public static Version Current => Normalize(Assembly.GetEntryAssembly()?.GetName().Version);

    /// <summary>릴리스 노트 1건(버전 + GitHub 릴리스 본문).</summary>
    public sealed class ReleaseNotes
    {
        public string Version { get; set; } = "";
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
    }

    private static string? Token
    {
        get
        {
            try { if (File.Exists(TokenFile)) { var t = File.ReadAllText(TokenFile).Trim(); if (t.Length > 0) return t; } }
            catch { }
            return string.IsNullOrWhiteSpace(UpdateConfig.EmbeddedToken) ? null : UpdateConfig.EmbeddedToken;
        }
    }

    /// <summary>앱 시작 즉시(창 생성 전) 호출. staged 최신 exe 가 있으면 교체 스크립트 실행 후 true(→ 즉시 종료).</summary>
    public static bool TryApplyStagedUpdate()
    {
        try
        {
            if (!File.Exists(PendingExe) || !File.Exists(PendingVer)) return false;
            if (!Version.TryParse(File.ReadAllText(PendingVer).Trim(), out var pv)) { Cleanup(); return false; }
            if (Normalize(pv) <= Current) { Cleanup(); return false; }

            string? target = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(target)) return false;
            int pid = Environment.ProcessId;

            Directory.CreateDirectory(StageDir);

            // 교체가 확정된 시점에 노트를 applied 로 승격한다.
            // (pending_* 는 apply.cmd 가 지우므로, 살아남을 이름으로 옮겨 둔다.)
            PromoteNotes(pv);
            string script = Path.Combine(StageDir, "apply.cmd");
            File.WriteAllText(script,
                "@echo off\r\n" +
                ":wait\r\n" +
                $"tasklist /fi \"PID eq {pid}\" | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
                $"copy /y \"{PendingExe}\" \"{target}\" >nul\r\n" +
                $"del /q \"{PendingExe}\" >nul 2>&1\r\n" +
                $"del /q \"{PendingVer}\" >nul 2>&1\r\n" +
                $"start \"\" \"{target}\"\r\n" +
                "del /q \"%~f0\" >nul 2>&1\r\n");

            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return true;
        }
        catch { return false; }
    }

    /// <summary>백그라운드에서 최신 릴리스 확인 → 더 높으면 staging 에 exe 다운로드(다음 실행 때 적용).</summary>
    /// <param name="progress">
    /// 0~1 진행률(다운로드 기준). 시작 시 0, 완료 시 1 을 보낸다.
    /// 길이를 모르는 응답이면 중간값 없이 0 → 1 만 온다(진행바는 불확정 모드로 표시).
    /// </param>
    public static async Task CheckAndStageAsync(IProgress<double>? progress = null)
    {
        if (!UpdateConfig.Enabled) return;
        string? token = Token; // 공개 저장소면 토큰 없이 동작. 토큰이 있으면(비공개 대비) 인증에 사용.

        try
        {
            using var http = CreateClient(token);

            string api = $"https://api.github.com/repos/{UpdateConfig.Owner}/{UpdateConfig.Repo}/releases/latest";
            string json = await http.GetStringAsync(api);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Version? latest = ParseTag(root.GetProperty("tag_name").GetString());
            if (latest == null || latest <= Current) return;

            // 이미 같은(또는 더 높은) 버전을 받아 뒀으면 재다운로드 생략
            if (File.Exists(PendingVer) && Version.TryParse(File.ReadAllText(PendingVer).Trim(), out var staged)
                && Normalize(staged) >= latest) return;

            string? apiUrl = null, browserUrl = null;
            foreach (var a in root.GetProperty("assets").EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    apiUrl = a.GetProperty("url").GetString();                                  // asset API URL(인증 필요)
                    browserUrl = a.TryGetProperty("browser_download_url", out var b) ? b.GetString() : null; // 공개 직링크
                    break;
                }
            }

            // 공개 저장소: 인증 없는 직링크(browser_download_url)로 받는다.
            // 토큰이 있으면(비공개 대비) asset API URL + octet-stream 으로 받는다.
            bool useApi = !string.IsNullOrEmpty(token) && apiUrl != null;
            string? downloadUrl = useApi ? apiUrl : browserUrl;
            if (downloadUrl == null) return;

            using var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            req.Headers.Accept.Clear();
            if (useApi) req.Headers.Accept.ParseAdd("application/octet-stream");
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();

            Directory.CreateDirectory(StageDir);
            string part = PendingExe + ".part";
            progress?.Report(0);

            long? total = resp.Content.Headers.ContentLength;
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var fs = File.Create(part))
            {
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await src.ReadAsync(buffer)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, n));
                    read += n;
                    if (total is > 0) progress?.Report(Math.Min(1.0, (double)read / total.Value));
                }
            }
            progress?.Report(1);

            if (File.Exists(PendingExe)) File.Delete(PendingExe);
            File.Move(part, PendingExe);
            File.WriteAllText(PendingVer, latest.ToString());

            // 릴리스 노트 저장. 건너뛴 중간 버전이 있으면 그 내용까지 모아 담는다.
            await SavePendingNotesAsync(http, root, latest);

            // 받아 둔 즉시 알린다. 예전에는 여기서 끝내고 "다음 실행 때" 교체했기 때문에
            // 사용자가 앱을 두 번 켜야 새 버전이 됐다. 이제 이 시점에 바로 적용을 제안한다.
            try { UpdateStaged?.Invoke(latest); }
            catch (Exception ex) { Logger.Error("UpdateStaged handler threw.", ex); }
        }
        catch { /* 네트워크/권한/파싱 문제는 조용히 무시(앱 사용에 지장 없음) */ }
    }

    /// <summary>
    /// 새 버전을 받아 둔 직후 발생(백그라운드 스레드). 인자는 받아 둔 버전.
    /// 구독자는 UI 스레드로 마샬링해야 한다.
    /// </summary>
    public static event Action<Version>? UpdateStaged;

    /// <summary>
    /// 현재보다 높은 릴리스가 있으면 그 버전, 없거나 확인 실패면 null.
    /// 시작 시 "진행 창을 띄울지" 판단하려고 다운로드 전에 먼저 물어본다.
    /// </summary>
    public static async Task<Version?> FindNewerVersionAsync()
    {
        if (!UpdateConfig.Enabled) return null;
        try
        {
            using var http = CreateClient(Token);
            string api = $"https://api.github.com/repos/{UpdateConfig.Owner}/{UpdateConfig.Repo}/releases/latest";
            using var doc = JsonDocument.Parse(await http.GetStringAsync(api));
            Version? latest = ParseTag(doc.RootElement.GetProperty("tag_name").GetString());
            return latest != null && latest > Current ? latest : null;
        }
        catch { return null; }
    }

    // ── 마지막 업데이트 노트(설정에서 열람) ─────────────────────
    private static string LastNotesPath => Path.Combine(StageDir, "last_notes.json");

    /// <summary>적용된 노트를 보관한다(팝업 대신 설정에서 보여주기 위함).</summary>
    public static void SaveLastNotes(ReleaseNotes notes)
    {
        try
        {
            Directory.CreateDirectory(StageDir);
            File.WriteAllText(LastNotesPath, JsonSerializer.Serialize(notes));
        }
        catch (Exception ex) { Logger.Error("Failed to save last notes.", ex); }
    }

    /// <summary>보관된 마지막 업데이트 노트(없으면 null).</summary>
    public static ReleaseNotes? LoadLastNotes()
    {
        try
        {
            if (!File.Exists(LastNotesPath)) return null;
            return JsonSerializer.Deserialize<ReleaseNotes>(File.ReadAllText(LastNotesPath));
        }
        catch { return null; }
    }

    /// <summary>받아 둔 업데이트가 있는가(재시작하면 바로 적용 가능한 상태).</summary>
    public static bool HasStagedUpdate =>
        File.Exists(PendingExe) && File.Exists(PendingVer)
        && Version.TryParse(File.ReadAllText(PendingVer).Trim(), out var v)
        && Normalize(v) > Current;

    /// <summary>
    /// 최신 릴리스의 노트를 즉시 조회한다(설정창의 "최근 변경 내용 보기"용).
    /// 네트워크 실패 시 null.
    /// </summary>
    public static async Task<ReleaseNotes?> FetchLatestNotesAsync()
    {
        if (!UpdateConfig.Enabled) return null;
        try
        {
            using var http = CreateClient(Token);
            string api = $"https://api.github.com/repos/{UpdateConfig.Owner}/{UpdateConfig.Repo}/releases/latest";
            string json = await http.GetStringAsync(api);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Version? v = ParseTag(root.GetProperty("tag_name").GetString());
            return new ReleaseNotes
            {
                Version = (v ?? Current).ToString(3),
                Title = root.TryGetProperty("name", out var n) ? (n.GetString() ?? "").Trim() : "",
                Body = root.TryGetProperty("body", out var b)
                    ? (b.GetString() ?? "").Replace("\r\n", "\n").Trim()
                    : "",
            };
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to fetch latest release notes.", ex);
            return null;
        }
    }

    private static HttpClient CreateClient(string? token)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ThrowMe-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    /// <summary>
    /// 릴리스 노트를 pending_notes.json 으로 저장한다.
    ///
    /// 한 번에 여러 버전을 건너뛰는 경우(예: 1.5.0 → 1.8.0)를 위해 <b>그 사이의 모든 릴리스</b>
    /// 본문을 모아서 담는다. 예전에는 최신 릴리스 하나만 담아서 중간 버전의 변경 내용이
    /// 통째로 사라졌다.
    /// </summary>
    private static async Task SavePendingNotesAsync(HttpClient http, JsonElement latestRoot, Version latest)
    {
        try
        {
            var sections = await FetchNotesBetweenAsync(http, Current, latest);

            string title;
            string body;

            if (sections.Count > 1)
            {
                // 여러 버전을 건너뛴 경우: 최신 → 과거 순으로 모두 싣는다.
                title = $"v{sections[^1].Version} → v{sections[0].Version}";
                body = string.Join("\n\n", sections.Select(s =>
                    $"## v{s.Version}\n{s.Body}".TrimEnd()));
            }
            else if (sections.Count == 1)
            {
                title = sections[0].Title;
                body = sections[0].Body;
            }
            else
            {
                // 목록 조회에 실패하면 최신 릴리스 하나만이라도 담는다(기존 동작).
                title = latestRoot.TryGetProperty("name", out var n) ? (n.GetString() ?? "").Trim() : "";
                body = latestRoot.TryGetProperty("body", out var b)
                    ? (b.GetString() ?? "").Replace("\r\n", "\n").Trim()
                    : "";
            }

            var notes = new ReleaseNotes
            {
                Version = latest.ToString(3),
                Title = title,
                Body = body,
            };
            File.WriteAllText(PendingNotes, JsonSerializer.Serialize(notes));
        }
        catch (Exception ex) { Logger.Error("Failed to save pending release notes.", ex); }
    }

    /// <summary>
    /// <paramref name="from"/> 초과 ~ <paramref name="to"/> 이하인 릴리스들의 노트를
    /// 최신순으로 반환한다. 실패하면 빈 목록.
    /// </summary>
    private static async Task<List<ReleaseNotes>> FetchNotesBetweenAsync(
        HttpClient http, Version from, Version to)
    {
        var result = new List<ReleaseNotes>();
        try
        {
            string api = $"https://api.github.com/repos/{UpdateConfig.Owner}/{UpdateConfig.Repo}/releases?per_page=30";
            string json = await http.GetStringAsync(api);
            using var doc = JsonDocument.Parse(json);

            var items = new List<(Version V, ReleaseNotes N)>();
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                // 초안·프리릴리스는 제외
                if (rel.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) continue;
                if (rel.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) continue;

                Version? v = ParseTag(rel.GetProperty("tag_name").GetString());
                if (v == null || v <= from || v > to) continue;

                items.Add((v, new ReleaseNotes
                {
                    Version = v.ToString(3),
                    Title = rel.TryGetProperty("name", out var n) ? (n.GetString() ?? "").Trim() : "",
                    Body = rel.TryGetProperty("body", out var b)
                        ? (b.GetString() ?? "").Replace("\r\n", "\n").Trim()
                        : "",
                }));
            }

            result.AddRange(items.OrderByDescending(x => x.V).Select(x => x.N));
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to fetch release list; falling back to the latest release only.", ex);
        }
        return result;
    }

    /// <summary>exe 교체가 확정되면 pending_notes → applied_notes 로 옮긴다(버전 확정 기록).</summary>
    private static void PromoteNotes(Version applying)
    {
        try
        {
            ReleaseNotes notes;
            if (File.Exists(PendingNotes))
            {
                notes = JsonSerializer.Deserialize<ReleaseNotes>(File.ReadAllText(PendingNotes))
                        ?? new ReleaseNotes();
            }
            else
            {
                notes = new ReleaseNotes(); // 노트 없이 받은 경우에도 "업데이트됨"은 알린다.
            }

            notes.Version = Normalize(applying).ToString(3);
            File.WriteAllText(AppliedNotes, JsonSerializer.Serialize(notes));

            try { if (File.Exists(PendingNotes)) File.Delete(PendingNotes); } catch { }
        }
        catch (Exception ex) { Logger.Error("Failed to promote release notes.", ex); }
    }

    /// <summary>
    /// 방금 업데이트가 적용되어 보여줄 노트가 있으면 반환하고 파일을 지운다(1회만 표시).
    /// 기록된 버전이 지금 실행 중인 버전과 다르면 무시한다(교체 실패/롤백 대비).
    /// </summary>
    /// <summary>
    /// 받아 둔(아직 적용 전) 릴리스 노트를 읽는다. 지우지 않는다.
    /// 재시작을 묻는 창에서 "무엇이 바뀌는지" 미리 보여주는 데 쓴다.
    /// </summary>
    public static ReleaseNotes? TryReadPendingNotes()
    {
        try
        {
            if (!File.Exists(PendingNotes)) return null;
            return JsonSerializer.Deserialize<ReleaseNotes>(File.ReadAllText(PendingNotes));
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to read pending release notes.", ex);
            return null;
        }
    }

    /// <summary>
    /// 노트를 이미 보여줬다고 표시한다(재시작 확인창에서 읽은 경우).
    /// 승격할 것이 없으면 교체 후 같은 내용이 또 뜨지 않는다.
    /// </summary>
    public static void MarkNotesSeen()
    {
        try { if (File.Exists(PendingNotes)) File.Delete(PendingNotes); }
        catch (Exception ex) { Logger.Error("Failed to mark notes as seen.", ex); }
    }

    public static ReleaseNotes? TryConsumeAppliedNotes()
    {
        try
        {
            if (!File.Exists(AppliedNotes)) return null;

            var notes = JsonSerializer.Deserialize<ReleaseNotes>(File.ReadAllText(AppliedNotes));
            try { File.Delete(AppliedNotes); } catch { } // 성공/실패와 무관하게 1회성

            if (notes == null) return null;
            if (!Version.TryParse(notes.Version, out var nv)) return null;
            if (Normalize(nv) != Current) return null; // 실제로 이 버전이 떠 있을 때만

            return notes;
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to read applied release notes.", ex);
            return null;
        }
    }

    private static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim().TrimStart('v', 'V');
        return Version.TryParse(tag, out var v) ? Normalize(v) : null;
    }

    private static Version Normalize(Version? v) =>
        v == null ? new Version(0, 0, 0, 0)
                  : new Version(Math.Max(0, v.Major), Math.Max(0, v.Minor), Math.Max(0, v.Build), 0);

    private static void Cleanup()
    {
        try { if (File.Exists(PendingExe)) File.Delete(PendingExe); } catch { }
        try { if (File.Exists(PendingVer)) File.Delete(PendingVer); } catch { }
        try { if (File.Exists(PendingNotes)) File.Delete(PendingNotes); } catch { }
    }
}
