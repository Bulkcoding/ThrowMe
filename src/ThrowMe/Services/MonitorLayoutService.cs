using System.Runtime.InteropServices;
using System.Windows;
using WinFormsScreen = System.Windows.Forms.Screen;
using Microsoft.Win32;
using ThrowMe.Physics;
using Point = System.Windows.Point;

namespace ThrowMe.Services;

/// <summary>
/// 모니터 영역/작업 영역 정보를 관리하는 서비스.
/// 좌표는 모두 물리 스크린 픽셀(가상 데스크톱 좌표계, 음수 가능).
///
/// Phase 2: 주 모니터 작업영역만 물리 엔진에 제공한다.
/// Phase 3: Monitors 전체 목록과 IsInsideAny 를 이용해
///          인접 경계 통과/빈 공간 반사로 확장한다(로직 채우기 지점).
/// </summary>
public sealed class MonitorLayoutService : IDisposable, IWalkableArea
{
    /// <summary>모든 모니터의 작업 영역(작업표시줄 제외). 물리 픽셀.</summary>
    public IReadOnlyList<Rect> WorkingAreas { get; private set; } = Array.Empty<Rect>();

    /// <summary>주 모니터 작업 영역.</summary>
    public Rect PrimaryWorkingArea { get; private set; }

    /// <summary>모든 모니터를 감싸는 가상 데스크톱 경계(참고용).</summary>
    public Rect VirtualBounds { get; private set; }

    /// <summary>모니터 구성/해상도 변경 시 발생.</summary>
    public event EventHandler? LayoutChanged;

    private bool _disposed;

    public MonitorLayoutService()
    {
        Refresh();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>연결된 모니터 정보를 다시 조회한다.</summary>
    public void Refresh()
    {
        var areas = new List<Rect>();
        Rect primary = default;
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;

        // Win32 로 직접 조회한다. PerMonitorV2 프로세스에서 GetMonitorInfo 의 좌표는
        // 실제 물리 픽셀이라, 창 배치(SetWindowPos)가 쓰는 좌표계와 정확히 일치한다.
        // System.Windows.Forms.Screen 은 혼합 DPI(예: 4K 배율) 환경에서 좌표가 어긋나,
        // 보이지 않는 벽이나 모니터 사이 빈 틈을 만들어 공이 경계에서 막히는 원인이 됐다.
        foreach (var (work, isPrimary) in EnumerateWorkAreas())
        {
            areas.Add(work);
            if (isPrimary) primary = work;

            minX = Math.Min(minX, work.Left);
            minY = Math.Min(minY, work.Top);
            maxX = Math.Max(maxX, work.Right);
            maxY = Math.Max(maxY, work.Bottom);
        }

        // 방어: Win32 조회 실패 시 WinForms 로 폴백, 그것도 없으면 기본값.
        if (areas.Count == 0)
        {
            foreach (var screen in WinFormsScreen.AllScreens)
            {
                var wa = screen.WorkingArea;
                var rect = new Rect(wa.X, wa.Y, wa.Width, wa.Height);
                areas.Add(rect);
                if (screen.Primary) primary = rect;
                minX = Math.Min(minX, rect.Left);
                minY = Math.Min(minY, rect.Top);
                maxX = Math.Max(maxX, rect.Right);
                maxY = Math.Max(maxY, rect.Bottom);
            }
        }

        if (areas.Count == 0)
        {
            primary = new Rect(0, 0, 1920, 1080);
            areas.Add(primary);
            minX = 0; minY = 0; maxX = 1920; maxY = 1080;
        }
        else if (primary == default)
        {
            primary = areas[0];
        }

        WorkingAreas = areas;
        PrimaryWorkingArea = primary;
        VirtualBounds = new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>모든 모니터의 작업 영역(물리 픽셀)과 주 모니터 여부를 Win32 로 열거한다.</summary>
    private static List<(Rect Work, bool Primary)> EnumerateWorkAreas()
    {
        var result = new List<(Rect, bool)>();
        MonitorEnumProc cb = (IntPtr hMon, IntPtr hdc, ref RECT lprc, IntPtr data) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                var w = mi.rcWork;
                var rect = new Rect(w.Left, w.Top, Math.Max(0, w.Right - w.Left), Math.Max(0, w.Bottom - w.Top));
                bool isPrimary = (mi.dwFlags & MONITORINFOF_PRIMARY) != 0;
                result.Add((rect, isPrimary));
            }
            return true; // 계속 열거
        };
        try { EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero); }
        catch { /* 실패 시 폴백에서 처리 */ }
        return result;
    }

    // ── Win32 (다중 모니터 물리 좌표) ──────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    private const uint MONITORINFOF_PRIMARY = 0x1;

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprc, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    /// <summary>점이 어느 한 모니터의 작업영역에 포함되는가.</summary>
    public bool IsInsideAny(System.Windows.Point p)
    {
        foreach (var area in WorkingAreas)
        {
            if (p.X >= area.Left && p.X < area.Right && p.Y >= area.Top && p.Y < area.Bottom)
                return true;
        }
        return false;
    }

    /// <summary>
    /// 슬라임 사각형의 네 모서리 + 중심이 모두 어느 한 모니터에 포함되면 유효.
    /// 인접 모니터에 걸쳐 있어도(모서리가 서로 다른 모니터에 속함) 통과되고,
    /// 빈 좌표 영역이나 외곽으로 벗어나면 무효(벽)로 판정된다.
    /// </summary>
    public bool IsRectValid(Rect rect)
    {
        // 모니터 경계는 반열림 구간이라 우/하단 모서리는 아주 살짝 안쪽을 검사한다.
        const double eps = 0.5;
        double left = rect.Left;
        double top = rect.Top;
        double right = rect.Right - eps;
        double bottom = rect.Bottom - eps;
        double cx = rect.Left + rect.Width / 2.0;
        double cy = rect.Top + rect.Height / 2.0;

        return IsInsideAny(new Point(left, top))
            && IsInsideAny(new Point(right, top))
            && IsInsideAny(new Point(left, bottom))
            && IsInsideAny(new Point(right, bottom))
            && IsInsideAny(new Point(cx, cy));
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Refresh();
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }
}
