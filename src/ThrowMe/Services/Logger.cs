using System.IO;

namespace ThrowMe.Services;

/// <summary>
/// 파일 기반 경량 로거. 데이터 폴더(기본 C:ThrowMe)의 logs/ThrowMe.log 에 append 한다.
/// 로깅 자체 실패는 삼켜서 앱 동작에 영향을 주지 않는다.
/// </summary>
public static class Logger
{
    private static readonly object _lock = new();
    private static string? _path;

    private static string Path
    {
        get
        {
            if (_path == null)
            {
                string dir = System.IO.Path.Combine(AppPaths.Roaming, "logs");
                Directory.CreateDirectory(dir);
                _path = System.IO.Path.Combine(dir, "ThrowMe.log");
            }
            return _path;
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : $"{message}\n{ex}");

    /// <summary>이 크기를 넘으면 .1 로 밀어내고 새로 시작한다(무한정 커지는 것 방지).</summary>
    private const long MaxBytes = 1024 * 1024;

    private static void Write(string level, string message)
    {
        try
        {
            lock (_lock)
            {
                Rotate();
                File.AppendAllText(Path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 로깅 실패는 무시(앱 흐름 우선).
        }
    }

    /// <summary>
    /// 로그가 커지면 직전 것 하나만 남기고 새로 쓴다(ThrowMe.log → ThrowMe.1.log).
    /// 예전에는 회전이 없어 파일이 무한정 자랐고, 정작 필요한 최근 기록을 찾기 어려웠다.
    /// </summary>
    private static void Rotate()
    {
        try
        {
            var fi = new FileInfo(Path);
            if (!fi.Exists || fi.Length < MaxBytes) return;

            string prev = System.IO.Path.ChangeExtension(Path, null) + ".1.log";
            if (File.Exists(prev)) File.Delete(prev);
            File.Move(Path, prev);
        }
        catch { /* 회전 실패해도 로깅은 계속한다 */ }
    }
}
