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

        Logger.Info("ThrowMe starting.");

        // 이전 실행에서 받아 둔 업데이트가 있으면, 창을 만들기 전에 교체·재시작하고 즉시 종료.
        if (UpdateService.TryApplyStagedUpdate())
        {
            Logger.Info("Applying staged update; restarting.");
            Shutdown();
            return;
        }

        _store = new SettingsStore();
        _settings = _store.Load();

        _monitorService = new MonitorLayoutService();

        _slimeWindow = new SlimeWindow(_settings, _monitorService);
        if (_settings.SlimeVisible)
            _slimeWindow.Show();

        _tray = new TrayIconService(
            _settings,
            openSettings: () => _slimeWindow?.OpenSettingsPublic(),
            resetPosition: () => _slimeWindow?.ResetPositionPublic(),
            exit: Shutdown);

        // 설정 변경 시 디바운스 자동 저장.
        _store.AttachAutoSave(_settings);

        // 방금 업데이트가 적용됐다면 노트를 설정에서 볼 수 있게 보관만 한다(팝업 없음).
        StashAppliedNotes();

        // 새 버전이 있으면 묻지 않고 진행바만 띄워 받고, 적용 후 자동 재시작.
        _ = RunStartupUpdateAsync();

        Logger.Info("ThrowMe started.");
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
            Logger.Info("ThrowMe exited.");
        }
        catch (Exception ex)
        {
            Logger.Error("Error during shutdown.", ex);
        }
        base.OnExit(e);
    }
}
