using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ThrowMe.Services;

/// <summary>
/// 테두리 없는 창(<c>WindowStyle=None</c>)에 <b>OS(DWM) 가 직접 그리는</b> 둥근 모서리·그림자·1px 테두리를 입힌다.
///
/// 왜 WPF 로 안 그리고 DWM 에 맡기나:
/// <list type="bullet">
///   <item>WPF <c>DropShadowEffect</c> 는 창 안쪽에 그리는 흉내라서, 그림자가 번질 여백을 창 크기에
///         포함시켜야 하고 다른 창들의 OS 그림자와 진하기·번짐이 달라 튄다.</item>
///   <item>DWM 그림자는 창 밖에 컴포지터가 그리므로 여백이 필요 없고, 다른 앱 창과 똑같이 보인다.</item>
///   <item>모서리도 DWM 이 창 자체를 잘라주므로, 자식 컨트롤 Background 가 모서리 밖으로
///         삐져나오는 문제가 원천적으로 없다(WPF Clip 이 필요 없다).</item>
/// </list>
///
/// <b>전제: <c>AllowsTransparency</c> 가 반드시 false 여야 한다.</b> true 면 창이 레이어드 윈도우가 되어
/// DWM 이 모서리·그림자를 그려주지 않는다(그래서 창 배경도 투명이 아닌 실제 색이어야 한다).
///
/// 둥근 모서리·테두리 색은 Windows 11(빌드 22000+) 기능이다. Windows 10 에서는 호출이 조용히 무시되어
/// 각진 모서리로 보이지만, 그 외 동작은 같다.
/// </summary>
internal static class DwmChrome
{
    private const int DWMWA_NCRENDERING_POLICY = 2;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;

    private const int DWMNCRP_ENABLED = 2;   // NC 영역이 없는 창에도 DWM 렌더링(=그림자)을 켠다
    private const int DWMWCP_ROUND = 2;      // 기본 라운딩(작은 창용 ROUNDSMALL 은 3)

    /// <summary>테마의 카드 테두리색과 같은 톤(#3C3C44). COLORREF 는 0x00BBGGRR 순서.</summary>
    private const int BorderColorRef = 0x00443C3C;

    /// <summary>
    /// 창에 DWM 그림자·둥근 모서리·테두리를 입힌다. 창 핸들이 있어야 하므로
    /// <see cref="Window.SourceInitialized"/> 이후에 호출해야 한다(그 전에는 조용히 넘어간다).
    /// </summary>
    public static void Apply(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        Set(hwnd, DWMWA_NCRENDERING_POLICY, DWMNCRP_ENABLED);
        Set(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
        Set(hwnd, DWMWA_BORDER_COLOR, BorderColorRef);
    }

    /// <summary><see cref="Window.SourceInitialized"/> 에 자동으로 걸어 준다. 생성자에서 한 줄로 쓰는 용도.</summary>
    public static void AttachTo(Window window) =>
        window.SourceInitialized += (_, _) => Apply(window);

    private static void Set(IntPtr hwnd, int attribute, int value)
    {
        // 지원하지 않는 OS 에서는 실패 HRESULT 만 돌아온다. 창은 그대로 잘 뜨므로 무시한다.
        try { DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)); }
        catch (DllNotFoundException) { /* dwmapi 없는 환경 */ }
        catch (EntryPointNotFoundException) { /* 무시 */ }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
