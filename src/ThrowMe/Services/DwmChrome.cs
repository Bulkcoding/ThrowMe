using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
// WinForms 도 함께 쓰는 프로젝트라 System.Drawing 과 이름이 겹친다. WPF 쪽으로 고정한다.
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace ThrowMe.Services;

/// <summary>
/// 테두리 없는 창(<c>WindowStyle=None</c>)의 외곽(둥근 모서리·그림자·1px 테두리)을 입힌다.
///
/// OS 에 따라 방식이 갈린다. 한 방식으로 둘 다 만족시킬 수 없어서 그렇다 —
/// DWM 이 그려 주려면 창이 레이어드 윈도우가 <b>아니어야</b> 하고(<c>AllowsTransparency=false</c>),
/// WPF 가 직접 둥글게 자르려면 모서리 바깥이 투명해야 해서 <c>AllowsTransparency=true</c> 가 필요하다.
///
/// <list type="bullet">
///   <item><b>Windows 11</b>(빌드 22000+): OS(DWM)가 그린다. 그림자가 창 밖에 그려져 다른 앱 창과
///         똑같이 보이고, 모서리는 DWM 이 창 자체를 잘라 주므로 자식 Background 가 모서리 밖으로
///         삐져나올 여지가 없다.</item>
///   <item><b>Windows 10</b>: DWM 라운딩·테두리색 속성이 없다. 그래서 예전 방식 —
///         투명 창 + 둥근 <see cref="UIElement.Clip"/> + WPF 그림자 — 으로 같은 모양을 낸다.
///         v1.11.1 까지 쓰던 방식이라 Win10 사용자에게는 보이는 변화가 없다.</item>
/// </list>
///
/// 두 경로 모두 <b>창을 띄우기 전</b>(생성자에서 <c>InitializeComponent()</c> 직후)에 호출해야 한다.
/// <c>AllowsTransparency</c> 는 창 핸들이 만들어진 뒤에는 바꿀 수 없다.
/// </summary>
internal static class DwmChrome
{
    private const int DWMWA_NCRENDERING_POLICY = 2;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;

    private const int DWMNCRP_ENABLED = 2;   // NC 영역이 없는 창에도 DWM 렌더링(=그림자)을 켠다
    private const int DWMWCP_ROUND = 2;      // 기본 라운딩(작은 창용 ROUNDSMALL 은 3)

    /// <summary>테마의 카드 테두리와 같은 톤. Win11 은 COLORREF(0x00BBGGRR), Win10 은 Brush 로 쓴다.</summary>
    private const int BorderColorRef = 0x00443C3C;
    private static readonly Color BorderColor = Color.FromRgb(0x3C, 0x3C, 0x44);

    /// <summary>Win10 대체 경로에서 쓰는 값. 모양이 Win11 기본 라운딩과 비슷하게 보이도록 맞췄다.</summary>
    private const double LegacyCorner = 8;
    private const double LegacyShadowMargin = 16;

    /// <summary>
    /// Windows 11 이상인가. app.manifest 에 Windows 10 supportedOS 가 선언돼 있어
    /// <see cref="Environment.OSVersion"/> 이 실제 빌드를 그대로 돌려준다.
    /// </summary>
    private static bool IsWindows11 =>
        Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22000;

    /// <summary>
    /// 창에 외곽(둥근 모서리·그림자·테두리)을 입힌다.
    /// <c>InitializeComponent()</c> 직후, 창을 띄우기 전에 호출할 것.
    /// </summary>
    public static void AttachTo(Window window)
    {
        if (IsWindows11)
        {
            // 핸들이 생긴 뒤에야 속성을 걸 수 있다.
            window.SourceInitialized += (_, _) => ApplyDwm(window);
            return;
        }

        ApplyLegacy(window);
    }

    // ── Windows 11: OS 가 그린다 ──────────────────────────────────────────
    private static void ApplyDwm(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        Set(hwnd, DWMWA_NCRENDERING_POLICY, DWMNCRP_ENABLED);
        Set(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, DWMWCP_ROUND);
        Set(hwnd, DWMWA_BORDER_COLOR, BorderColorRef);
    }

    // ── Windows 10: WPF 가 직접 그린다(v1.11.1 방식) ──────────────────────
    /// <summary>
    /// 창을 투명하게 바꾸고, 기존 내용을 [그림자 레이어 + 둥근 Clip Border] 로 감싼다.
    /// XAML 은 Win11 기준 한 벌만 유지하고 여기서만 갈아 끼우려고 코드로 구성한다.
    /// </summary>
    private static void ApplyLegacy(Window window)
    {
        if (window.Content is not UIElement content) return;

        // 창 배경색(WinBg)을 그대로 본문 Border 가 이어받는다. 창 자체는 투명해져야 한다.
        Brush background = window.Background ?? Brushes.Black;
        window.AllowsTransparency = true;
        window.Background = Brushes.Transparent;

        window.Content = null;

        // 그림자 전용 레이어. 본문에 Effect 를 걸면 UI 전체가 중간 비트맵으로 렌더돼 글자가 흐려진다.
        var shadow = new Border
        {
            CornerRadius = new CornerRadius(LegacyCorner),
            Background = background,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                BlurRadius = 18, ShadowDepth = 3, Opacity = 0.42, Color = Colors.Black,
            },
        };

        // 본문. CornerRadius 는 자기 배경만 둥글게 칠하므로, 자식까지 자르려면 둥근 Clip 이 필요하다.
        var root = new Border
        {
            CornerRadius = new CornerRadius(LegacyCorner),
            Background = background,
            BorderBrush = new SolidColorBrush(BorderColor),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = content,
        };
        root.SizeChanged += (_, e) => root.Clip = new RectangleGeometry(
            new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), LegacyCorner, LegacyCorner);

        var host = new Grid { Margin = new Thickness(LegacyShadowMargin) };
        host.Children.Add(shadow);
        host.Children.Add(root);
        window.Content = host;

        // 그림자 여백만큼 창을 키워 본문 크기를 그대로 유지한다.
        // SizeToContent 가 걸린 축은 내용에 맞춰 알아서 늘어나므로 건드리지 않는다.
        bool autoWidth = window.SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight;
        bool autoHeight = window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight;
        if (!autoWidth && !double.IsNaN(window.Width)) window.Width += LegacyShadowMargin * 2;
        if (!autoHeight && !double.IsNaN(window.Height)) window.Height += LegacyShadowMargin * 2;
    }

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
