using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;

namespace ThrowMe.Services;

/// <summary>
/// <c>ThrowMe.exe --hook &lt;event&gt; &lt;port&gt;</c> 로 실행됐을 때의 처리.
///
/// Claude Code 훅이 stdin 으로 주는 이벤트 JSON 을 그대로 로컬 서버(<see cref="CliStateServer"/>)로
/// 넘기되, <b>그 세션이 도는 터미널 창 핸들</b>을 함께 붙인다. 훅 명령은 CLI(claude)의 자식으로
/// 실행되므로, 우리(이 프로세스)의 조상 프로세스를 거슬러 올라가면 실제 터미널 창을 찾을 수 있다.
/// 이렇게 모아 둔 핸들로 나중에 세션을 클릭하면 그 터미널 창을 앞으로 가져온다.
///
/// 창 핸들 조회는 조금 무거우므로(스냅샷) 세션 시작·프롬프트 제출처럼 드문 이벤트에만 이 경로를 쓴다.
/// 나머지 상태 이벤트는 가벼운 curl 로 그대로 보낸다(<see cref="ClaudeHooksInstaller"/>).
/// </summary>
internal static class HookRelay
{
    public static void Run(string ev, int port)
    {
        try
        {
            string body;
            using (var stdin = Console.OpenStandardInput())
            using (var sr = new StreamReader(stdin, Encoding.UTF8))
                body = sr.ReadToEnd();

            long hwnd = (long)FindTerminalWindow();

            string url = $"http://127.0.0.1:{port}/state?event={Uri.EscapeDataString(ev)}&hwnd={hwnd}";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var content = new StringContent(body ?? "", Encoding.UTF8, "application/json");
            http.PostAsync(url, content).Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 훅은 조용히 실패해야 한다 — Claude Code 동작을 막지 않는다.
        }
    }

    /// <summary>이 프로세스의 조상에서 실제 창을 가진 터미널을 찾는다. 없으면 부모 콘솔 창으로 폴백.</summary>
    private static IntPtr FindTerminalWindow()
    {
        try
        {
            var parent = BuildParentMap();
            int cur = Environment.ProcessId;
            for (int depth = 0; depth < 10; depth++)
            {
                if (!parent.TryGetValue(cur, out int ppid) || ppid == 0 || ppid == cur) break;
                cur = ppid;
                try
                {
                    using var p = Process.GetProcessById(cur);
                    if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle; // 터미널(WindowsTerminal 등)
                }
                catch { /* 이미 종료된 조상 — 계속 올라간다 */ }
            }
        }
        catch { }

        // 폴백: 부모(=claude)의 콘솔 창(conhost 등).
        try
        {
            if (AttachConsole(unchecked((uint)-1)))
            {
                IntPtr h = GetConsoleWindow();
                FreeConsole();
                if (h != IntPtr.Zero) return h;
            }
        }
        catch { }

        return IntPtr.Zero;
    }

    /// <summary>pid → 부모 pid 맵(프로세스 스냅샷).</summary>
    private static Dictionary<int, int> BuildParentMap()
    {
        var map = new Dictionary<int, int>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE) return map;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snap, ref e))
            {
                do { map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID; }
                while (Process32Next(snap, ref e));
            }
        }
        finally { CloseHandle(snap); }
        return map;
    }

    // ── Win32 ───────────────────────────────────────────────
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll")]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll")]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
}
