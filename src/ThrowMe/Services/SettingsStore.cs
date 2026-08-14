using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using ThrowMe.Models;
using Timer = System.Threading.Timer;

namespace ThrowMe.Services;

/// <summary>
/// AppSettings 를 JSON 파일로 저장/로드한다. (System.Text.Json — NuGet 불필요)
/// 경로: %APPDATA%/ThrowMe/settings.json
///
/// AttachAutoSave 로 설정 변경(PropertyChanged) 시 디바운스 저장하며,
/// 종료 시 Save 로 최종 상태를 확실히 남긴다.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new() { WriteIndented = true };
    private readonly object _lock = new();

    private Timer? _debounce;
    private AppSettings? _tracked;
    private bool _disposed;

    private const int DebounceMs = 400;

    /// <summary>프로필 이름(--profile). 비우면 settings.json, 지정 시 settings.&lt;profile&gt;.json.</summary>
    public static string Profile { get; set; } = "";

    public SettingsStore()
    {
        string dir = AppPaths.Roaming;
        _path = System.IO.Path.Combine(dir,
            string.IsNullOrWhiteSpace(Profile) ? "settings.json" : $"settings.{Profile}.json");
    }

    /// <summary>저장된 설정을 읽는다. 없거나 손상 시 기본값 반환.</summary>
    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                string json = File.ReadAllText(_path);
                var s = JsonSerializer.Deserialize<AppSettings>(json, _options);
                if (s != null)
                {
                    // 손상된 값(예: 던지기 가중치 0)을 되살린다. 자동 저장은 아직 연결되기 전이라
                    // 여기서 바로 저장해야 파일까지 낫는다(안 그러면 매 실행마다 같은 값을 다시 고친다).
                    if (s.RepairInvalidValues()) Save(s);
                    Logger.Info("Settings loaded.");
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Settings load failed; using defaults.", ex);
        }
        return new AppSettings();
    }

    /// <summary>즉시 저장(임시 파일 → 교체로 손상 위험 최소화).</summary>
    public void Save(AppSettings settings)
    {
        try
        {
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, _options));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.Error("Settings save failed.", ex);
        }
    }

    /// <summary>설정 변경을 구독해 디바운스 저장한다.</summary>
    public void AttachAutoSave(AppSettings settings)
    {
        _tracked = settings;
        settings.PropertyChanged += OnChanged;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        lock (_lock)
        {
            if (_disposed) return;
            _debounce?.Dispose();
            _debounce = new Timer(_ =>
            {
                var s = _tracked;
                if (s != null) Save(s);
            }, null, DebounceMs, Timeout.Infinite);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_tracked != null) _tracked.PropertyChanged -= OnChanged;
        lock (_lock) { _debounce?.Dispose(); }
    }
}
