using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using MessageBoxResult = System.Windows.MessageBoxResult;
using ThrowMe.Models;
using ThrowMe.Network;
using ThrowMe.Services;
using ThrowMe.Views;

namespace ThrowMe;

public partial class App : Application
{
    private MonitorLayoutService? _monitorService;
    private SlimeWindow? _slimeWindow;
    private SettingsStore? _store;
    private TrayIconService? _tray;
    private AppSettings? _settings;

    /// <summary>중복 실행 방지용. 프로세스가 살아 있는 동안 잡고 있는다.</summary>
    private System.Threading.Mutex? _singleInstance;

    /// <summary>
    /// 이 사용자 세션에서 하나만 실행되도록 잠근다. 이미 떠 있으면 false.
    /// 업데이트 교체 직후처럼 이전 프로세스가 막 끝나는 중일 수 있어 잠깐 재시도한다.
    /// </summary>
    private bool AcquireSingleInstance(string? profile)
    {
        // Local\ 접두사 = 로그인 세션 단위. 프로필을 쓰면 프로필별로 하나씩 허용(테스트).
        string name = string.IsNullOrWhiteSpace(profile)
            ? @"Local\ThrowMe.SingleInstance"
            : $@"Local\ThrowMe.SingleInstance.{profile}";

        for (int attempt = 0; attempt < 12; attempt++) // 최대 약 3초
        {
            try
            {
                var mutex = new System.Threading.Mutex(initiallyOwned: true, name, out bool created);
                if (created) { _singleInstance = mutex; return true; }
                mutex.Dispose();
            }
            catch (Exception ex)
            {
                Logger.Error("Single-instance check failed; allowing start.", ex);
                return true; // 잠금 실패로 앱을 못 켜게 하지는 않는다
            }
            System.Threading.Thread.Sleep(250);
        }
        return false;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 오프라인 미리보기: `--render-preview <출력폴더>` 로 실행하면 스킨/골대를 PNG로 저장하고 종료.
        if (e.Args.Length >= 2 && e.Args[0] == "--render-preview")
        {
            try { PreviewRenderer.Run(e.Args[1]); }
            catch (Exception ex) { Logger.Error("preview render failed", ex); }
            Shutdown();
            return;
        }

        RegisterGlobalExceptionHandlers();

        // 데이터 폴더 확정 + 예전 이름(ThrowMe) 폴더에서 이관. 설정을 읽기 전에 끝내야 한다.
        AppPaths.Initialize();
        foreach (string line in AppPaths.TakePendingLog())
            Logger.Info(line);

        // --profile=<이름> : 설정 파일을 분리해 한 PC에서 여러 인스턴스를 서로 다른 노드로 띄운다(테스트용).
        // 반드시 설정을 읽기 전에 적용해야 한다.
        string? profile = e.Args
            .FirstOrDefault(a => a.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2)[1].Trim();
        if (!string.IsNullOrWhiteSpace(profile))
        {
            SettingsStore.Profile = profile;
            AuthService.Profile = profile;
            Logger.Info($"Using settings profile '{profile}'.");
        }

        // 중복 실행 방지. 여러 개가 켜지면 공이 여러 마리로 보이고, 같은 설정 파일을 서로
        // 덮어쓰며, 방에도 같은 이름으로 중복 접속해 서로를 밀어낸다.
        // 프로필을 지정한 경우(테스트용 다중 인스턴스)는 프로필별로 하나씩 허용한다.
        if (!AcquireSingleInstance(profile))
        {
            Logger.Info("Another instance is already running; exiting.");
            Shutdown();
            return;
        }

        LogEnvironment();
        UpdateService.LogPreviousApplyResult();
        NotifyBlockedUpdateOnce();

        // 이전 실행에서 받아 둔 업데이트가 있으면, 창을 만들기 전에 교체·재시작하고 즉시 종료.
        if (UpdateService.TryApplyStagedUpdate())
        {
            Logger.Info("Applying staged update; restarting.");
            Shutdown();
            return;
        }

        _store = new SettingsStore();
        _settings = _store.Load();

        // 잡기를 Ctrl + 좌클릭으로 한 번 맞춘다(예전 설정 파일 대응).
        _settings.MigrateCatchHotkeyOnce();

        _monitorService = new MonitorLayoutService();

        _slimeWindow = new SlimeWindow(_settings, _monitorService);
        if (_settings.SlimeVisible)
            _slimeWindow.Show();

        _tray = new TrayIconService(
            _settings,
            // 트레이 콜백은 WinForms 스레드에서 실행되어 WPF 의 전역 예외 처리가 잡지 못한다.
            // 여기서 감싸지 않으면 설정창 생성 중 오류가 그대로 ".NET 오류 대화상자"로 튀어나온다.
            openSettings: () => SafeTrayAction(() => _slimeWindow?.OpenSettingsPublic(), "openSettings"),
            resetPosition: () => SafeTrayAction(() => _slimeWindow?.ResetPositionPublic(), "resetPosition"),
            exit: () => SafeTrayAction(Shutdown, "exit"));

        // 설정 변경 시 디바운스 자동 저장.
        _store.AttachAutoSave(_settings);

        // 방금 업데이트가 적용됐다면 노트를 설정에서 볼 수 있게 보관만 한다(팝업 없음).
        StashAppliedNotes();

        // 버전이 바뀌었으면 바로가기 아이콘이 옛 그림으로 남지 않게 셸 캐시를 갱신한다.
        RefreshIconCacheIfVersionChanged();

        // 새 버전이 있으면 묻지 않고 진행바만 띄워 받고, 적용 후 자동 재시작.
        _ = RunStartupUpdateAsync();

        Logger.Info("ThrowMe started.");
    }

    /// <summary>
    /// 업데이트 교체가 막혔으면 한 번만 알려 준다.
    ///
    /// 조용히 넘기면 사용자는 "왜 계속 옛 버전이지?" 를 알 길이 없고, 매번 알리면 잔소리가 된다.
    /// 그래서 막힌 버전당 한 번만 띄운다. 상세 원인은 로그에 남아 있다.
    /// </summary>
    private void NotifyBlockedUpdateOnce()
    {
        try
        {
            var blocked = UpdateService.BlockedVersion;
            if (blocked == null || !UpdateService.ShouldNotifyBlocked(blocked)) return;

            // 창이 아직 없으므로 잠깐 뒤에 띄운다(토스트도 창이다).
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                try
                {
                    ToastWindow.Show($"업데이트를 적용하지 못했어요 (v{blocked.ToString(3)})",
                        "실행 파일을 덮어쓰지 못했습니다. 백신 예외에 넣거나, ThrowMe.exe 를 쓰기 가능한 폴더로 옮겨 주세요.",
                        9);
                }
                catch (Exception ex) { Logger.Error("Blocked-update toast failed.", ex); }
            };
            t.Start();
        }
        catch (Exception ex) { Logger.Error("Blocked-update notify failed.", ex); }
    }

    /// <summary>
    /// 실행 환경을 한 줄로 남긴다. 원격에서 문의가 오면 가장 먼저 필요한 정보들이다
    /// — 어떤 버전이 어디서 실행 중인지, 데이터가 어디에 쌓이는지, 화면 구성은 어떤지.
    /// </summary>
    private void LogEnvironment()
    {
        try
        {
            string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "(unknown)";
            var screens = System.Windows.Forms.Screen.AllScreens;
            string mons = string.Join(", ", screens.Select(s =>
                $"{s.Bounds.Width}x{s.Bounds.Height}@{s.Bounds.X},{s.Bounds.Y}{(s.Primary ? "*" : "")}"));

            Logger.Info($"ThrowMe starting. v{UpdateService.Current.ToString(3)} | OS {Environment.OSVersion.Version} " +
                        $"| user {Environment.UserName} | exe '{exe}'");
            Logger.Info($"Data folder '{AppPaths.Roaming}' | monitors {screens.Length}: {mons}");
        }
        catch (Exception ex)
        {
            Logger.Error("Environment log failed.", ex);
        }
    }

    /// <summary>
    /// 시작 시 업데이트 처리. 새 버전이 있으면 <b>묻지 않고</b> 진행 창만 띄워 내려받고,
    /// 교체 후 자동으로 다시 시작한다. 무엇이 달라졌는지는 설정에서 확인한다.
    /// 업데이트가 없으면 아무것도 표시하지 않는다(창을 만들지도 않음).
    /// </summary>
    private async Task RunStartupUpdateAsync()
    {
        UpdateProgressWindow? win = null;
        try
        {
            var latest = await UpdateService.FindNewerVersionAsync();
            if (latest == null) return; // 최신이거나 확인 실패 → 조용히 통과

            Logger.Info($"Update available: v{latest}. Downloading…");
            win = new UpdateProgressWindow(latest);
            win.Show();

            var progress = new Progress<double>(p => win?.SetProgress(p));
            await UpdateService.CheckAndStageAsync(progress);

            if (!UpdateService.HasStagedUpdate)
            {
                Logger.Error("Update download did not produce a staged file.");
                win.Close();
                return;
            }

            win.SetApplying();
            await Task.Delay(400); // 진행 창이 '적용 중'으로 바뀐 걸 보여 줄 최소 시간

            if (UpdateService.TryApplyStagedUpdate())
            {
                Logger.Info($"Applying update v{latest}; restarting.");
                Shutdown(); // 교체 스크립트가 종료를 기다렸다가 새 버전으로 다시 실행한다
            }
            else
            {
                Logger.Error("Staged update could not be applied (write permission?).");
                win.Close();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Startup update failed.", ex);
            try { win?.Close(); } catch { /* 무시 */ }
        }
    }


    /// <summary>
    /// 방금 업데이트가 적용됐다면 노트를 "마지막 업데이트 노트"로 보관한다.
    /// 팝업은 띄우지 않는다 — 설정 → 일반 → "최근 변경 내용 보기" 에서 언제든 볼 수 있다.
    /// </summary>
    private void StashAppliedNotes()
    {
        try
        {
            var notes = UpdateService.TryConsumeAppliedNotes();
            if (notes == null) return;
            UpdateService.SaveLastNotes(notes);
            Logger.Info($"Updated to v{notes.Version}. (notes available in settings)");
        }
        catch (Exception ex)
        {
            Logger.Error("Release notes stash failed.", ex);
        }
    }

    /// <summary>
    /// 실행된 버전이 지난번과 다르면 탐색기 아이콘 캐시를 갱신한다.
    ///
    /// 업데이트는 exe 를 같은 경로에 덮어쓰므로, 바로가기의 경로·아이콘 인덱스가 그대로다.
    /// 셸은 바뀔 이유가 없다고 보고 캐시된 옛 아이콘을 계속 써서, 아이콘을 바꿔 배포해도
    /// 바탕화면 바로가기만 예전 그림으로 남았다. 버전이 바뀐 첫 실행에만 한 번 알린다.
    /// </summary>
    private void RefreshIconCacheIfVersionChanged()
    {
        if (_settings == null) return;
        try
        {
            string current = UpdateService.Current.ToString(3);
            if (_settings.LastRunVersion == current) return;

            // 첫 실행(기록 없음)에도 한 번 돌려 둔다 — 새로 받아 온 경우가 대부분이라 해될 것이 없다.
            string? exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
            ShellIconRefresh.Refresh(exe);

            _settings.LastRunVersion = current;
            _store?.Save(_settings); // 다음 실행에서 또 돌지 않게 즉시 기록
            Logger.Info($"Icon cache refreshed for v{current}.");
        }
        catch (Exception ex)
        {
            Logger.Error("Icon cache refresh check failed.", ex);
        }
    }

    /// <summary>트레이 메뉴 동작을 감싼다. 실패해도 앱이 죽거나 오류 대화상자가 뜨지 않게.</summary>
    private static void SafeTrayAction(Action action, string name)
    {
        try { action(); }
        catch (Exception ex) { Logger.Error($"Tray action '{name}' failed.", ex); }
    }

    /// <summary>
    /// 종료 시 남아 있는 모든 창을 닫는다. 테마가 띄운 보조 창(볼링 레인/핀/점수판, 농구골대,
    /// 이펙트 오버레이 등)이 어떤 이유로든 정리되지 않으면 공만 사라지고 그것들만 화면에 남는다.
    /// </summary>
    private static void CloseAllRemainingWindows()
    {
        try
        {
            // Close() 가 컬렉션을 바꾸므로 복사본을 순회한다.
            foreach (Window w in Current.Windows.Cast<Window>().ToList())
            {
                try { w.Close(); }
                catch (Exception ex) { Logger.Error($"Failed to close window '{w.GetType().Name}'.", ex); }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("CloseAllRemainingWindows failed.", ex);
        }
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("Dispatcher unhandled exception.", args.Exception);
            args.Handled = true; // 앱이 죽지 않도록
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Logger.Error("Domain unhandled exception.", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _slimeWindow?.ShutdownCleanup();
            _tray?.Dispose();

            if (_store != null && _settings != null)
                _store.Save(_settings); // 종료 시 최종 상태 확정 저장
            _store?.Dispose();

            _monitorService?.Dispose();

            // 마지막 안전망: 그래도 남아 있는 창(볼링 레인·핀·점수판, 농구골대, 오버레이 등)을 닫는다.
            // 테마가 띄우는 창이 늘어나도 여기서 한 번에 걸러진다.
            CloseAllRemainingWindows();

            // 다음 실행이 곧바로 켜질 수 있도록 잠금을 놓는다(업데이트 재시작 포함).
            try { _singleInstance?.ReleaseMutex(); } catch { /* 소유 아님 */ }
            _singleInstance?.Dispose();
            _singleInstance = null;

            Logger.Info("ThrowMe exited.");
        }
        catch (Exception ex)
        {
            Logger.Error("Error during shutdown.", ex);
        }
        base.OnExit(e);
    }
}
