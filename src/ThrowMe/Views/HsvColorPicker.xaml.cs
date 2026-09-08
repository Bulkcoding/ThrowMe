using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using UserControl = System.Windows.Controls.UserControl;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;

namespace ThrowMe.Views;

/// <summary>
/// 색상(H)·채도(S)·밝기(V)로 임의 색을 고르는 컨트롤. 외부 패키지 없이 WPF 기본 요소만 쓴다.
/// 사각형: 가로 = 채도(0→1), 세로 = 밝기(위 1 → 아래 0). 띠: 색상 0~360.
/// </summary>
public partial class HsvColorPicker : UserControl
{
    private double _h, _s = 1.0, _v = 1.0;
    private bool _dragging;
    private bool _suppress;   // 프로그램이 값을 바꾸는 동안 이벤트를 막는다

    public Color Color => FromHsv(_h, _s, _v);

    public event EventHandler<Color>? ColorChanged;

    public HsvColorPicker()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    /// <summary>초기값 지정. ColorChanged 를 올리지 않는다.</summary>
    public void SetColor(Color c)
    {
        (_h, _s, _v) = ToHsv(c);
        Refresh();
    }

    // ── 입력 ─────────────────────────────────────────────
    private void OnSvDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        SvBox.CaptureMouse();
        PickSv(e.GetPosition(SvBox));
    }

    private void OnSvMove(object sender, MouseEventArgs e)
    {
        if (_dragging) PickSv(e.GetPosition(SvBox));
    }

    private void OnSvUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        SvBox.ReleaseMouseCapture();
    }

    private void PickSv(Point p)
    {
        _s = Math.Clamp(p.X / SvBox.ActualWidth, 0, 1);
        _v = 1.0 - Math.Clamp(p.Y / SvBox.ActualHeight, 0, 1);
        Refresh();
        ColorChanged?.Invoke(this, Color);
    }

    private void OnHueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppress || SvHue == null) return;
        _h = e.NewValue % 360.0;
        Refresh();
        ColorChanged?.Invoke(this, Color);
    }

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyHex(); e.Handled = true; }
    }

    private void OnHexLostFocus(object sender, RoutedEventArgs e) => ApplyHex();

    private void ApplyHex()
    {
        string t = HexBox.Text.Trim().TrimStart('#');
        if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            Refresh(); // 잘못된 입력은 현재 색으로 되돌린다
            return;
        }
        var c = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        (_h, _s, _v) = ToHsv(c);
        Refresh();
        ColorChanged?.Invoke(this, Color);
    }

    // ── 표시 ─────────────────────────────────────────────
    private void Refresh()
    {
        _suppress = true;
        try
        {
            SvHue.Fill = new LinearGradientBrush(Colors.White, FromHsv(_h, 1, 1), 0); // 가로 흰→색상
            HueSlider.Value = _h;
            var c = Color;
            Preview.Background = new SolidColorBrush(c);
            HexBox.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            double w = SvBox.ActualWidth > 0 ? SvBox.ActualWidth : 216;
            double h = SvBox.ActualHeight > 0 ? SvBox.ActualHeight : 160;
            Canvas.SetLeft(SvMarker, _s * w - SvMarker.Width / 2);
            Canvas.SetTop(SvMarker, (1 - _v) * h - SvMarker.Height / 2);
        }
        finally { _suppress = false; }
    }

    // ── 변환 ─────────────────────────────────────────────
    /// <summary>h 0~360, s·v 0~1 → 불투명 색.</summary>
    public static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = (int)(h / 60) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb(To255(r + m), To255(g + m), To255(b + m));
    }

    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        double h = 0;
        if (d > 1e-9)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
            if (h < 0) h += 360;
        }
        double s = max <= 0 ? 0 : d / max;
        return (h, s, max);
    }

    private static byte To255(double x) => (byte)Math.Clamp((int)Math.Round(x * 255.0), 0, 255);
}
