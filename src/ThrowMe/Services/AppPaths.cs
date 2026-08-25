using System.IO;
using System.Linq;

namespace ThrowMe.Services;

/// <summary>
/// 사용자 데이터 폴더를 한곳에서 결정하고, 예전 이름(Slimey) 폴더에서 한 번만 이관한다.
///
/// 앱 이름이 Slimey → ThrowMe 로 바뀌면서 폴더도 옮겨야 하는데, 그냥 바꾸면
/// 기존 사용자의 설정·커스텀 스킨·방 접속 정보가 전부 초기화된다.
/// 그래서 새 폴더가 없고 옛 폴더가 있으면 통째로 복사해 온다.
///
/// - 이관은 <b>복사</b>다(삭제하지 않는다). 실패하거나 옛 버전으로 되돌아가도 원본이 남는다.
/// - 한 번 이관하면 새 폴더가 생기므로 다시 실행되지 않는다.
/// - staged 업데이트(update 폴더)는 일시적 데이터라 받아 둔 exe 를 가져오지 않고 다시 받게 둔다.
///   단 릴리스 노트만은 예외로 가져온다(<see cref="UpdateFilesToMigrate"/> 참고).
///
/// [주의] 이 클래스는 <see cref="Logger"/> 를 쓰지 않는다. Logger 가 로그 폴더를 얻으려고
/// 이 클래스를 호출하므로 순환이 되고, 더 나쁘게는 로그 파일이 새 폴더를 먼저 만들어
/// "새 폴더가 이미 있음"으로 판정되어 이관이 통째로 건너뛰어진다.
/// 이관 결과는 <see cref="TakePendingLog"/> 로 꺼내 앱이 대신 기록한다.
/// </summary>
public static class AppPaths
{
    private const string NewName = "ThrowMe";
    private const string OldName = "Slimey";

    /// <summary>
    /// staged 업데이트 폴더. 받아 둔 exe·스크립트는 일시적 데이터라 이관하지 않고 다시 받게 두되,
    /// 아래 파일만 예외로 가져온다.
    /// </summary>
    private const string UpdateDir = "update";

    /// <summary>
    /// update 폴더에서 유일하게 이관하는 파일.
    ///
    /// 이름이 바뀌는 이 업데이트에서는 <b>구버전(Slimey)이 교체를 수행</b>하므로,
    /// 릴리스 노트가 옛 경로에 남는다. 이걸 가져오지 않으면 "앱 이름이 ThrowMe 로 바뀌었다"는
    /// 가장 중요한 안내가 사용자에게 뜨지 않는다.
    /// </summary>
    private static readonly string[] UpdateFilesToMigrate = { "applied_notes.json" };

    private static readonly object Gate = new();
    private static bool _migrated;
    private static readonly List<string> PendingLog = new();

    /// <summary>
    /// 모아 두는 데이터 폴더. 설정·로그·스킨·방 정보·업데이트 임시파일이 전부 여기 들어간다.
    /// 예전에는 %APPDATA% 와 %LOCALAPPDATA% 로 흩어져 있어 사용자가 찾기 어려웠다.
    /// </summary>
    public const string SharedRoot = @"C:\ThrowMe";

    /// <summary>실제로 쓰기로 결정된 데이터 폴더. <see cref="Initialize"/> 가 정한다.</summary>
    private static string? _root;

    /// <summary>
    /// 설정·스킨·로그가 들어가는 폴더. 기본은 <see cref="SharedRoot"/>,
    /// 만들 수 없으면 예전 위치(%APPDATA%\ThrowMe)로 물러난다.
    /// </summary>
    public static string Roaming => Root;

    /// <summary>방 접속 정보·업데이트 staging 폴더. 이제 <see cref="Roaming"/> 과 같은 곳이다.</summary>
    public static string Local => Root;

    /// <summary>
    /// 데이터 폴더를 결정한다. C:\ThrowMe 를 만들어 보고, 실제로 쓸 수 있을 때만 채택한다.
    ///
    /// 폴더 생성만 되고 쓰기가 막히는 경우가 있다(회사 GPO·EDR). 그래서 임시 파일을
    /// 한 번 써 보고 지워 확인한다 — 여기서 잘못 판단하면 설정이 통째로 저장되지 않는다.
    /// 실패하면 예전 경로를 그대로 쓰므로 사용자는 아무것도 잃지 않는다.
    /// </summary>
    private static string Root
    {
        get
        {
            lock (Gate)
            {
                if (_root != null) return _root;

                string legacy = Ensure(Environment.SpecialFolder.ApplicationData);
                try
                {
                    Directory.CreateDirectory(SharedRoot);
                    string probe = System.IO.Path.Combine(SharedRoot, ".write_test");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    _root = SharedRoot;
                    Note($"Data folder: '{SharedRoot}'.");
                }
                catch (Exception ex)
                {
                    _root = legacy;
                    Note($"'{SharedRoot}' unusable ({ex.GetType().Name}: {ex.Message}); using '{legacy}'.");
                    return _root;
                }

                // 예전 위치(Roaming/Local)에 있던 데이터를 한 번만 옮겨 온다.
                MigrateIntoRoot(legacy);
                MigrateIntoRoot(Ensure(Environment.SpecialFolder.LocalApplicationData));
                return _root;
            }
        }
    }

    /// <summary>
    /// 예전 폴더의 내용을 새 데이터 폴더로 복사한다(덮어쓰지 않는다).
    ///
    /// <b>복사</b>라서 원본이 남는다 — 옛 버전으로 되돌아가도 설정을 잃지 않는다.
    /// 이미 새 폴더에 같은 이름이 있으면 새 것이 이긴다(두 번째 호출에서 Local 이
    /// Roaming 것을 덮어쓰지 않게 하는 장치이기도 하다).
    /// </summary>
    private static void MigrateIntoRoot(string oldDir)
    {
        try
        {
            if (_root == null || !Directory.Exists(oldDir)) return;
            if (string.Equals(oldDir, _root, StringComparison.OrdinalIgnoreCase)) return;

            int files = 0;
            foreach (string src in Directory.GetFiles(oldDir))
            {
                string dst = System.IO.Path.Combine(_root, System.IO.Path.GetFileName(src));
                if (File.Exists(dst)) continue;
                File.Copy(src, dst);
                files++;
            }
            foreach (string dir in Directory.GetDirectories(oldDir))
            {
                string name = System.IO.Path.GetFileName(dir);
                string dst = System.IO.Path.Combine(_root, name);
                if (Directory.Exists(dst)) continue;
                // update 폴더는 일시적 데이터라 노트만 가져온다(기존 규칙 유지).
                CopyTree(dir, dst, name.Equals(UpdateDir, StringComparison.OrdinalIgnoreCase)
                    ? UpdateFilesToMigrate : null);
                files++;
            }
            if (files > 0) Note($"Moved user data '{oldDir}' -> '{_root}' ({files} entries).");
        }
        catch (Exception ex)
        {
            Note($"Migration from '{oldDir}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 이관을 미리 수행한다. 앱 시작 시 가장 먼저 호출한다
    /// (호출하지 않아도 첫 경로 접근 시 지연 수행되지만, 순서를 확실히 하려면 명시 호출이 낫다).
    /// </summary>
    public static void Initialize()
    {
        _ = Roaming;
        _ = Local;
    }

    /// <summary>이관 과정에서 쌓인 메시지를 꺼낸다(한 번만). 앱이 Logger 로 기록한다.</summary>
    public static IReadOnlyList<string> TakePendingLog()
    {
        lock (Gate)
        {
            var copy = PendingLog.ToArray();
            PendingLog.Clear();
            return copy;
        }
    }

    private static string Ensure(Environment.SpecialFolder folder)
    {
        lock (Gate)
        {
            if (!_migrated)
            {
                // Roaming/Local 을 한 번에 처리한다. 어느 쪽이 먼저 호출되든
                // 두 폴더의 존재 여부를 "아무 폴더도 만들기 전에" 판정해야 한다.
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

                _migrated = true; // 재진입 방지(예외가 나도 다시 시도하지 않는다)
                TryMigrate(Path.Combine(appData, OldName), Path.Combine(appData, NewName));
                TryMigrate(Path.Combine(localData, OldName), Path.Combine(localData, NewName));
            }
        }

        string target = Path.Combine(Environment.GetFolderPath(folder), NewName);
        try { Directory.CreateDirectory(target); }
        catch (Exception ex) { Note($"Failed to create data dir '{target}': {ex.Message}"); }
        return target;
    }

    /// <summary>옛 폴더가 있고 새 폴더가 아직 없으면 복사한다.</summary>
    private static void TryMigrate(string oldDir, string newDir)
    {
        try
        {
            if (Directory.Exists(newDir)) return;   // 이미 새 폴더 사용 중
            if (!Directory.Exists(oldDir)) return;  // 이관할 것이 없음(신규 설치)

            CopyTree(oldDir, newDir);
            Note($"Migrated user data '{oldDir}' -> '{newDir}'.");
        }
        catch (Exception ex)
        {
            // 이관 실패는 치명적이지 않다 — 기본값으로 시작하면 된다.
            Note($"Failed to migrate user data from '{oldDir}': {ex.Message}");
        }
    }

    /// <param name="only">지정하면 이 파일명만 복사한다(하위 폴더는 건너뜀).</param>
    private static void CopyTree(string src, string dst, string[]? only = null)
    {
        Directory.CreateDirectory(dst);

        foreach (string file in Directory.GetFiles(src))
        {
            string name = Path.GetFileName(file);
            if (only != null && !only.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;

            try { File.Copy(file, Path.Combine(dst, name), overwrite: false); }
            catch (Exception ex) { Note($"Failed to copy '{file}': {ex.Message}"); }
        }

        if (only != null) return; // 부분 이관은 하위 폴더까지 내려가지 않는다

        foreach (string dir in Directory.GetDirectories(src))
        {
            string name = Path.GetFileName(dir);
            bool isUpdateDir = name.Equals(UpdateDir, StringComparison.OrdinalIgnoreCase);
            CopyTree(dir, Path.Combine(dst, name), isUpdateDir ? UpdateFilesToMigrate : null);
        }
    }

    /// <summary>Logger 를 쓸 수 없으므로 메시지를 모아 둔다(Gate 안에서만 호출).</summary>
    private static void Note(string message)
    {
        System.Diagnostics.Debug.WriteLine("[AppPaths] " + message);
        PendingLog.Add(message);
    }
}
