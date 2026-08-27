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

    /// <summary>직전 실행이 어떤 버전으로 교체를 시도했는지(버전\n대상경로). 결과 판정용.</summary>
    private static string ApplyAttempt => Path.Combine(StageDir, "apply_attempt.txt");

    /// <summary>apply.cmd 가 남기는 교체 결과("ok" 또는 "fail &lt;errorlevel&gt;").</summary>
    private static string ApplyResult => Path.Combine(StageDir, "apply_result.txt");

    /// <summary>
    /// 교체에 실패한 기록(버전\n시각\n연속 실패 횟수). 무한 재시작 방지용.
    /// <b>버전이 아니라 이 PC 단위</b>로 본다 — 예전에는 실패한 버전만 막아서, 새 릴리스가
    /// 올라오면 "다른 버전이니 다시 해 보자"며 방어가 풀렸다. 원인(덮어쓰기 불가)은 그대로인데
    /// 릴리스를 낼 때마다 160MB 다운로드와 재시작이 한 바퀴씩 다시 돌았다.
    /// </summary>
    private static string ApplyBlocked => Path.Combine(StageDir, "apply_blocked.txt");

    /// <summary>실패한 교체를 다시 시도해 보기까지 기다리는 시간. 그 사이 권한·백신을 손볼 수 있다.</summary>
    private static readonly TimeSpan BlockRetryAfter = TimeSpan.FromHours(24);

    /// <summary>이만큼 연속으로 실패하면 시간이 지나도 자동 재시도를 하지 않는다(수동 교체 안내).</summary>
    private const int HardBlockAfterFailures = 2;

    /// <summary>기록된 연속 실패 횟수. 기록이 없으면 0.</summary>
    private static int BlockedFailureCount
    {
        get
        {
            try
            {
                if (!File.Exists(ApplyBlocked)) return 0;
                string[] lines = File.ReadAllText(ApplyBlocked).Split('\n');
                // 3번째 줄이 없으면 옛 형식(2줄) — 1회 실패로 본다.
                if (lines.Length > 2 && int.TryParse(lines[2].Trim(), out int n)) return Math.Max(1, n);
                return 1;
            }
            catch { return 0; }
        }
    }

    /// <summary>
    /// 지금 이 PC 에서 자동 교체가 막혀 있는가. 버전과 무관하다.
    ///
    /// 한 번 실패했을 때는 <see cref="BlockRetryAfter"/> 뒤에 한 번 더 해 본다
    /// (그 사이 사용자가 권한이나 백신 예외를 고쳤을 수 있다).
    /// <see cref="HardBlockAfterFailures"/> 회 연속 실패하면 자동 시도를 아예 멈춘다 —
    /// 그 PC 는 구조적으로 덮어쓸 수 없는 자리에 있는 것이므로, 계속 두면 매번 무한 재시작이 된다.
    /// </summary>
    private static bool IsApplyBlockedNow()
    {
        try
        {
            if (!File.Exists(ApplyBlocked)) return false;

            if (BlockedFailureCount >= HardBlockAfterFailures) return true;

            string[] lines = File.ReadAllText(ApplyBlocked).Split('\n');
            if (lines.Length > 1 && DateTime.TryParse(lines[1].Trim(), out var at)
                && DateTime.Now - at > BlockRetryAfter)
            {
                Logger.Info("Retrying update after previous failure (24h passed).");
                try { File.Delete(ApplyBlocked); } catch { }
                return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 사용자가 직접 새 exe 로 갈아 끼웠으면 막아 둔 것을 푼다.
    /// 수동 교체는 <see cref="ApplyAttempt"/> 를 남기지 않으므로, 이걸 안 하면
    /// 손으로 고친 뒤에도 자동 업데이트가 영영 꺼진 채로 남는다.
    /// </summary>
    public static void ClearBlockIfSelfUpdated()
    {
        try
        {
            var blocked = BlockedVersion;
            if (blocked == null || Current < Normalize(blocked)) return;

            Logger.Info($"Update block cleared — 지금 실행 중인 v{Current.ToString(3)} 가 " +
                        $"막아 둔 v{blocked.ToString(3)} 이상입니다(수동 교체로 보입니다).");
            try { File.Delete(ApplyBlocked); } catch { }
            try { if (File.Exists(BlockedNotified)) File.Delete(BlockedNotified); } catch { }
        }
        catch { }
    }

    /// <summary>
    /// 실행 파일을 덮어쓸 수 있는 자리에 있는가(다운로드 전에 확인).
    ///
    /// Program Files 처럼 쓰기가 막힌 폴더에서 실행 중이면 교체는 무조건 실패한다.
    /// 그걸 모르고 160MB 를 먼저 받으면 "받고 → 실패 → 재시작" 만 반복한다.
    /// 폴더에 쓸 수 있다고 교체까지 보장되지는 않지만(백신이 exe 만 막을 수 있다),
    /// 확실히 안 되는 경우를 받기 전에 걸러 준다.
    /// </summary>
    public static bool CanReplaceSelf(out string reason)
    {
        try
        {
            string? target = Process.GetCurrentProcess().MainModule?.FileName;
            string? dir = string.IsNullOrEmpty(target) ? null : Path.GetDirectoryName(target);
            if (string.IsNullOrEmpty(dir)) { reason = "실행 파일 경로를 알 수 없음"; return false; }

            string probe = Path.Combine(dir, ".throwme_write_test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            reason = "";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 교체가 막힌 버전(사용자에게 안내할 값). 없으면 null.
    /// 앱이 시작할 때 이 값이 있으면 "왜 업데이트가 안 되는지" 알려 준다.
    /// </summary>
    public static Version? BlockedVersion
    {
        get
        {
            try
            {
                if (!File.Exists(ApplyBlocked)) return null;
                string first = File.ReadAllText(ApplyBlocked).Split('\n')[0].Trim();
                return Version.TryParse(first, out var v) ? v : null;
            }
            catch { return null; }
        }
    }

    /// <summary>안내를 이미 한 번 보여 준 버전인가(같은 내용을 매번 띄우지 않으려고).</summary>
    private static string BlockedNotified => Path.Combine(StageDir, "apply_blocked_notified.txt");

    /// <summary>이 버전에 대한 안내를 아직 안 보여 줬으면 true 를 돌려주고, 보여 준 것으로 표시한다.</summary>
    public static bool ShouldNotifyBlocked(Version v)
    {
        try
        {
            if (File.Exists(BlockedNotified)
                && Version.TryParse(File.ReadAllText(BlockedNotified).Trim(), out var done)
                && Normalize(done) == Normalize(v)) return false;
            File.WriteAllText(BlockedNotified, v.ToString());
            return true;
        }
        catch { return false; }
    }

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

            // 직전에 이 버전으로 교체하다 실패했다면 곧바로 또 시도하지 않는다.
            // 재시도하면 "실행 → 교체 실패 → 재시작" 이 무한히 도는 꼴이 된다.
            // 사용자가 권한·백신을 손볼 시간을 주고 하루 뒤에 한 번 더 해 본다.
            if (IsApplyBlockedNow())
            {
                Logger.Info($"Skipping apply of v{pv} — 이 PC 에서 교체가 {BlockedFailureCount}회 실패해 보류 중입니다.");
                return false;
            }

            string? target = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(target)) return false;
            int pid = Environment.ProcessId;

            Directory.CreateDirectory(StageDir);

            // 무엇을 어디로 교체하려 했는지 남긴다. 다음 실행에서 실제로 그 버전이 됐는지 확인한다
            // — 교체가 조용히 실패하면 매번 다시 받으며 같은 팝업이 반복되는데, 지금은 그 사실이
            //   어디에도 기록되지 않아 원격에서 원인을 알 수 없었다.
            try { File.WriteAllText(ApplyAttempt, $"{pv}\n{target}"); } catch { }
            Logger.Info($"Applying update v{pv} -> '{target}'.");

            // 교체가 확정된 시점에 노트를 applied 로 승격한다.
            // (pending_* 는 apply.cmd 가 지우므로, 살아남을 이름으로 옮겨 둔다.)
            PromoteNotes(pv);
            string script = Path.Combine(StageDir, "apply.cmd");

            // 스크립트 본문에는 경로를 넣지 않는다 — 전부 환경 변수로 넘긴다.
            //
            // cmd.exe 는 .cmd 파일을 UTF-8 이 아니라 시스템 코드페이지로 읽는다(한국어 Windows 는 949).
            // 그래서 한글이 든 경로를 본문에 그대로 쓰면 깨진 경로가 되어 copy 가 실패한다.
            // 실측: 바탕화면의 한글 폴더에 둔 exe 에서 errorlevel 1 로 실패하고, 폴더 자체는
            // 쓰기 가능했다. 이 PC 는 켤 때마다 받고 → 실패 → 재시작을 반복했다.
            // 환경 변수는 유니코드로 전달되므로, 본문을 ASCII 로만 유지하면 어떤 경로에서도 안전하다.
            File.WriteAllText(script,
                "@echo off\r\n" +
                ":wait\r\n" +
                $"tasklist /fi \"PID eq {pid}\" | find \"{pid}\" >nul && (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
                // copy 결과를 반드시 본다. 예전에는 결과와 무관하게 받아 둔 파일을 지워 버려서,
                // 교체가 실패해도 옛 버전이 그대로 다시 뜨고 다음 실행에 160MB 를 또 받았다.
                // 성공했을 때만 지우고, 실패하면 그대로 남겨 결과 파일에 기록한다.
                "copy /y \"%TM_SRC%\" \"%TM_DST%\" >nul 2>&1\r\n" +
                "set RC=%errorlevel%\r\n" +
                "if not \"%RC%\"==\"0\" goto failed\r\n" +
                ">\"%TM_RESULT%\" echo ok\r\n" +
                "del /q \"%TM_SRC%\" >nul 2>&1\r\n" +
                "del /q \"%TM_VER%\" >nul 2>&1\r\n" +
                "goto run\r\n" +
                ":failed\r\n" +
                ">\"%TM_RESULT%\" echo fail %RC%\r\n" +
                ":run\r\n" +
                "start \"\" \"%TM_DST%\"\r\n" +
                "del /q \"%~f0\" >nul 2>&1\r\n");

            var psi = new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            psi.Environment["TM_SRC"] = PendingExe;
            psi.Environment["TM_DST"] = target;
            psi.Environment["TM_VER"] = PendingVer;
            psi.Environment["TM_RESULT"] = ApplyResult;
            Process.Start(psi);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 직전 실행이 시도한 교체가 실제로 반영됐는지 확인해 로그에 남긴다(앱 시작 시 1회).
    /// 동작은 바꾸지 않는다 — 기록만 한다.
    ///
    /// 교체는 apply.cmd 의 copy 한 줄에 달려 있는데 그 결과를 아무도 보지 않는다.
    /// 권한·백신 등으로 copy 가 실패하면 옛 버전이 그대로 다시 뜨고, 받아 둔 파일은 지워져
    /// 다음 실행에 또 받는다(팝업 반복). 여기서 그 사실과 대상 폴더의 쓰기 가능 여부를 남긴다.
    /// </summary>
    public static void LogPreviousApplyResult()
    {
        try
        {
            if (!File.Exists(ApplyAttempt)) return;
            string[] lines = File.ReadAllText(ApplyAttempt).Split('\n');
            try { File.Delete(ApplyAttempt); } catch { }

            if (!Version.TryParse(lines[0].Trim(), out var attempted)) return;
            string target = lines.Length > 1 ? lines[1].Trim() : "(unknown)";

            // apply.cmd 가 남긴 결과(있으면 더 정확하다)
            string result = "(no result file)";
            try { if (File.Exists(ApplyResult)) result = File.ReadAllText(ApplyResult).Trim(); } catch { }
            try { if (File.Exists(ApplyResult)) File.Delete(ApplyResult); } catch { }

            if (Current >= Normalize(attempted))
            {
                Logger.Info($"Update applied: now v{Current.ToString(3)}. (apply result: {result})");
                try { if (File.Exists(ApplyBlocked)) File.Delete(ApplyBlocked); } catch { }
                try { if (File.Exists(BlockedNotified)) File.Delete(BlockedNotified); } catch { }
                return;
            }

            // 실패. 당분간 재시도하지 않는다 — 안 그러면 실행할 때마다
            // "교체 시도 → 실패 → 재시작" 이 무한히 돈다. 받아 둔 파일은 apply.cmd 가
            // 남겨 두었으므로 다시 받을 필요는 없다.
            // 연속 실패 횟수를 함께 센다. HardBlockAfterFailures 회를 넘기면 시간이 지나도
            // 자동 재시도를 하지 않는다(그 PC 는 구조적으로 덮어쓸 수 없는 자리에 있는 것이다).
            int fails = BlockedFailureCount + 1;
            try { File.WriteAllText(ApplyBlocked, $"{attempted}\n{DateTime.Now:O}\n{fails}"); } catch { }

            // 실패. 원인 후보를 함께 남겨 둔다 — 대부분 대상 폴더에 쓰기가 막힌 경우다.
            string writable = "unknown";
            try
            {
                string? dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir))
                {
                    string probe = Path.Combine(dir, ".throwme_write_test");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    writable = "yes";
                }
            }
            catch (Exception ex) { writable = $"no ({ex.GetType().Name})"; }

            string hold = fails >= HardBlockAfterFailures
                ? "연속 실패가 쌓여 자동 재시도를 멈춥니다(직접 받아 교체해야 합니다)."
                : $"{BlockRetryAfter.TotalHours}시간 동안 재시도를 보류합니다.";
            Logger.Error($"Update did NOT apply. expected v{attempted} but running v{Current.ToString(3)}. " +
                         $"apply result: {result}. target='{target}', target folder writable={writable}. " +
                         $"실행 파일을 덮어쓰지 못했습니다(권한/백신 가능성). 연속 실패 {fails}회. {hold}");
        }
        catch (Exception ex) { Logger.Error("Apply-result check failed.", ex); }
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

            // 최신 버전 확인은 API 를 쓰지 않는다(IP 당 시간당 60회 한도 회피).
            Version? latest = await FetchLatestTagAsync();
            if (latest == null || latest <= Current) return;

            // 이 PC 에서 교체가 막혀 있으면 버전과 무관하게 받지 않는다. 예전에는 실패한 버전만
            // 막아서, 새 릴리스가 나올 때마다 160MB 를 다시 받고 재시작을 한 바퀴씩 돌았다.
            if (IsApplyBlockedNow())
            {
                Logger.Info($"Skipping download of v{latest} — 이 PC 에서 교체가 막혀 보류 중입니다.");
                return;
            }

            // 덮어쓸 수 없는 자리에서 실행 중이면 받아 봐야 소용없다. 받기 전에 거른다.
            if (!CanReplaceSelf(out string why))
            {
                Logger.Error($"Skipping download of v{latest} — 실행 파일 폴더에 쓸 수 없습니다({why}). " +
                             "쓰기 가능한 폴더로 옮기거나 직접 받아 교체해야 합니다.");
                return;
            }

            // 이미 같은(또는 더 높은) 버전을 받아 뒀으면 재다운로드 생략
            if (File.Exists(PendingVer) && Version.TryParse(File.ReadAllText(PendingVer).Trim(), out var staged)
                && Normalize(staged) >= latest) return;

            // 자산 이름은 우리가 정하므로(ThrowMe.exe) 직링크를 만들어 받는다 — API 불필요.
            string downloadUrl = DirectAssetUrl(latest);
            using var req = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
            req.Headers.Accept.Clear();
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

            // 릴리스 노트는 API 가 필요하다. 한도에 걸려 실패해도 업데이트 자체는 이미 끝났으므로
            // 여기서 막지 않는다(노트는 설정 탭에서 다시 받아올 수 있다).
            try { await SavePendingNotesAsync(http, latest); }
            catch (Exception ex) { Logger.Info($"Release notes not saved ({ex.GetType().Name}); update continues."); }

            // 받아 둔 즉시 알린다. 예전에는 여기서 끝내고 "다음 실행 때" 교체했기 때문에
            // 사용자가 앱을 두 번 켜야 새 버전이 됐다. 이제 이 시점에 바로 적용을 제안한다.
            try { UpdateStaged?.Invoke(latest); }
            catch (Exception ex) { Logger.Error("UpdateStaged handler threw.", ex); }
        }
        catch (Exception ex)
        {
            // 조용히 넘기되 원인은 남긴다(예전엔 통째로 삼켜서 왜 업데이트가 안 되는지 알 수 없었다).
            Logger.Error("Update check/stage failed.", ex);
        }
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
        // 여기서 null 을 돌려주면 앱은 아무것도 하지 않고 조용히 지나간다. 그래서 왜 안 받았는지를
        // 갈래마다 남긴다 — 남기지 않으면 "업데이트가 안 된다"는 문의에 로그로 답할 수가 없다.
        // 시작할 때 한 번만 부르므로 실행당 한 줄이다.
        if (!UpdateConfig.Enabled)
        {
            Logger.Info("Update check: 자동 업데이트가 꺼져 있습니다.");
            return null;
        }

        Version? latest = await FetchLatestTagAsync();
        if (latest == null)
        {
            Logger.Info("Update check: 최신 버전을 확인하지 못했습니다(네트워크 또는 차단).");
            return null;
        }

        if (latest <= Current)
        {
            // 릴리스를 막 올린 직후에는 여기 걸린다 — 버전 확인에 쓰는 /releases/latest 리다이렉트가
            // 몇 분 늦게 갱신되기 때문이다(설정의 업데이트 노트는 API 라 즉시 보인다).
            Logger.Info($"Update check: 최신 v{latest.ToString(3)} / 실행 중 v{Current.ToString(3)} — 받을 것이 없습니다.");
            return null;
        }

        // 이 PC 에서 교체가 막혀 있거나 덮어쓸 수 없는 자리면 "새 버전 있음"으로 보고하지 않는다
        // → 진행 팝업이 뜨지 않고, 받지도 않는다.
        if (IsApplyBlockedNow())
        {
            Logger.Info($"Update check: v{latest.ToString(3)} 가 있지만 이 PC 에서 교체가 " +
                        $"{BlockedFailureCount}회 연속 실패해 보류 중입니다.");
            return null;
        }

        if (!CanReplaceSelf(out string why))
        {
            Logger.Info($"Update check: v{latest.ToString(3)} 가 있지만 실행 파일 폴더에 쓸 수 없습니다({why}).");
            return null;
        }

        return latest;
    }

    /// <summary>
    /// 최신 릴리스 태그를 알아낸다.
    ///
    /// <b>API 를 쓰지 않는다.</b> 인증 없는 GitHub API 는 <b>IP 당 시간당 60회</b>라,
    /// 한 사무실에서 여러 PC 가 같은 공인 IP 를 쓰면 금방 소진된다. 그러면 업데이트 확인이
    /// 조용히 실패해 구버전에 머물게 된다(실제로 그런 일이 있었다).
    ///
    /// 대신 <c>/releases/latest</c> 웹 URL 이 최신 태그로 302 리다이렉트하는 것을 이용한다.
    /// 이건 일반 웹 요청이라 API 한도와 무관하다. 실패하면 API 로 폴백한다.
    /// </summary>
    private static async Task<Version?> FetchLatestTagAsync()
    {
        // 1) 리다이렉트 방식(한도 없음)
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ThrowMe-Updater");

            string url = $"https://github.com/{UpdateConfig.Owner}/{UpdateConfig.Repo}/releases/latest";
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

            string? location = resp.Headers.Location?.ToString();
            if (!string.IsNullOrEmpty(location))
            {
                // .../releases/tag/v1.12.0
                int i = location.LastIndexOf('/');
                if (i >= 0 && i + 1 < location.Length)
                {
                    Version? v = ParseTag(location[(i + 1)..]);
                    if (v != null) return v;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Info($"Latest-tag redirect lookup failed ({ex.GetType().Name}); falling back to API.");
        }

        // 2) API 폴백(한도에 걸릴 수 있음 — 걸리면 원인을 로그로 남긴다)
        try
        {
            using var http = CreateClient(Token);
            string api = $"https://api.github.com/repos/{UpdateConfig.Owner}/{UpdateConfig.Repo}/releases/latest";
            using var resp = await http.GetAsync(api);
            if ((int)resp.StatusCode == 403 || (int)resp.StatusCode == 429)
            {
                Logger.Error($"GitHub API rate limit hit ({(int)resp.StatusCode}); update check skipped this time.");
                return null;
            }
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return ParseTag(doc.RootElement.GetProperty("tag_name").GetString());
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to determine latest version.", ex);
            return null;
        }
    }

    /// <summary>릴리스 자산 직링크. 자산 이름은 우리가 정하므로 API 없이 만들 수 있다.</summary>
    private static string DirectAssetUrl(Version v) =>
        $"https://github.com/{UpdateConfig.Owner}/{UpdateConfig.Repo}/releases/download/v{v.ToString(3)}/ThrowMe.exe";

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
    /// <summary>
    /// 릴리스 목록을 최신순으로 가져온다(설정 → 업데이트 노트 탭에서 전체 이력 표시용).
    /// 실패하면 빈 목록.
    /// </summary>
    public static async Task<List<ReleaseNotes>> FetchAllReleasesAsync(int max = 30)
    {
        var list = new List<ReleaseNotes>();
        if (!UpdateConfig.Enabled) return list;
        try
        {
            using var http = CreateClient(Token);
            string api = $"https://api.github.com/repos/{UpdateConfig.Owner}/{UpdateConfig.Repo}" +
                         $"/releases?per_page={Math.Clamp(max, 1, 100)}";
            using var doc = JsonDocument.Parse(await http.GetStringAsync(api));

            foreach (var r in doc.RootElement.EnumerateArray())
            {
                // 초안·프리릴리스는 건너뛴다(사용자에게 배포된 것만).
                if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
                if (r.TryGetProperty("prerelease", out var p) && p.GetBoolean()) continue;

                Version? v = ParseTag(r.GetProperty("tag_name").GetString());
                list.Add(new ReleaseNotes
                {
                    Version = v?.ToString(3) ?? (r.GetProperty("tag_name").GetString() ?? "").TrimStart('v'),
                    Title = r.TryGetProperty("name", out var n) ? (n.GetString() ?? "").Trim() : "",
                    Body = r.TryGetProperty("body", out var b)
                        ? (b.GetString() ?? "").Replace("\r\n", "\n").Trim()
                        : "",
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to fetch release list.", ex);
        }
        return list;
    }

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
    private static async Task SavePendingNotesAsync(HttpClient http, Version latest)
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
                // 목록 조회 실패(API 한도 등) — 노트 없이 버전만 남긴다.
                // 설정 → 업데이트 노트 에서 나중에 다시 받아올 수 있다.
                title = $"v{latest.ToString(3)}";
                body = "";
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
