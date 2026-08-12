using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ThrowMe.Models;
using ThrowMe.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using MessageBox = System.Windows.MessageBox;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Size = System.Windows.Size;

namespace ThrowMe.Views;

/// <summary>
/// 공 표면에 직접 그리는 창. WPF 내장 <see cref="InkCanvas"/> 로 자유곡선을 받고,
/// 확정 시 그린 영역을 PNG(512×512)로 렌더해 <see cref="SkinImageStore"/> 에 저장한다.
///
/// 저장 대상은 XAML 의 <c>DrawArea</c> 하나뿐 — 안내 원·격자 배경은 밖에 두어 결과에 섞이지 않는다.
/// 결과는 정사각형으로 저장하고, 원형 클립은 공을 그리는 쪽(SlimeWindow)이 담당한다.
/// </summary>
public partial class SkinDrawWindow : Window
{
    /// <summary>저장 해상도(정사각). 공은 최대 180px 이라 이 정도면 충분히 선명하다.</summary>
    private const int OutputSize = 512;

    private static readonly Color[] PaletteColors =
    {
        Color.FromRgb(0x1C, 0x1C, 0x22), // 먹
        Color.FromRgb(0xFF, 0xFF, 0xFF), // 흰
        Color.FromRgb(0xE8, 0x3D, 0x3D), // 빨강
        Color.FromRgb(0xF2, 0x92, 0x1F), // 주황
        Color.FromRgb(0xF5, 0xD3, 0x2B), // 노랑
        Color.FromRgb(0x4E, 0xD1, 0x7A), // 초록
        Color.FromRgb(0x33, 0xC2, 0xD6), // 청록
        Color.FromRgb(0x3D, 0x7B, 0xE8), // 파랑
        Color.FromRgb(0x8B, 0x6F, 0xD6), // 보라
        Color.FromRgb(0xFF, 0x9E, 0xC4), // 분홍
    };

    private readonly SlimeSkinKind _kind;
    private readonly List<Border> _swatches = new();
    private Color _color = PaletteColors[0];
    private bool _erasing;

    /// <summary>적용을 눌러 저장까지 끝났는가.</summary>
    public bool Saved { get; private set; }

    public SkinDrawWindow(SlimeSkinKind kind, string themeName)
    {
        _kind = kind;
        InitializeComponent();
        DwmChrome.AttachTo(this); // 설정창과 같은 둥근 모서리·그림자·테두리

        TitleText.Text = $"{themeName} — 공에 그리기";
        Checker.Fill = MakeCheckerBrush();
        BuildPalette();
        ApplyBrush();

        // 이미 커스텀 이미지가 있으면 배경으로 깔아 그 위에 이어 그릴 수 있게 한다.
        BgImage.Source = SkinImageStore.Load(kind);
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    // ── 색 팔레트 ───────────────────────────────────────────
    private void BuildPalette()
    {
        foreach (var c in PaletteColors)
        {
            var swatch = new Border
            {
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 6, 8, 0),
                CornerRadius = new CornerRadius(15),
                Background = new SolidColorBrush(c),
                BorderThickness = new Thickness(2.5),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = c,
            };
            swatch.MouseLeftButtonUp += (_, _) =>
            {
                _color = c;
                _erasing = false;   // 색을 고르면 자연스럽게 펜으로 돌아온다
                ApplyBrush();
            };
            _swatches.Add(swatch);
            Palette.Children.Add(swatch);
        }
        HighlightSwatch();
    }

    private void HighlightSwatch()
    {
        var accent = (Brush)FindResource("Accent");
        foreach (var s in _swatches)
            s.BorderBrush = !_erasing && (Color)s.Tag! == _color ? accent : Brushes.Transparent;
    }

    // ── 붓 / 지우개 ─────────────────────────────────────────
    private void ApplyBrush()
    {
        double t = Thickness?.Value ?? 10;

        if (_erasing)
        {
            // 점 단위 지우개: 획 전체가 아니라 지나간 부분만 지운다.
            Ink.EditingMode = InkCanvasEditingMode.EraseByPoint;
            Ink.EraserShape = new EllipseStylusShape(t * 1.6, t * 1.6);
        }
        else
        {
            Ink.EditingMode = InkCanvasEditingMode.Ink;
            Ink.DefaultDrawingAttributes = new DrawingAttributes
            {
                Color = _color,
                Width = t,
                Height = t,
                FitToCurve = true,      // 손떨림 완화(부드러운 곡선)
                IsHighlighter = false,
            };
        }

        if (EraserBtn != null) EraserBtn.Content = _erasing ? "펜으로" : "지우개";
        HighlightSwatch();
    }

    private void OnThicknessChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Ink == null) return; // InitializeComponent 도중 초기 값 설정 무시
        ApplyBrush();
    }

    private void OnToggleEraser(object sender, RoutedEventArgs e)
    {
        _erasing = !_erasing;
        ApplyBrush();
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        var strokes = Ink.Strokes;
        if (strokes.Count > 0) strokes.RemoveAt(strokes.Count - 1);
    }

    private void OnClearAll(object sender, RoutedEventArgs e)
    {
        Ink.Strokes.Clear();
        BgImage.Source = null; // 불러온 이미지까지 완전히 비운다
    }

    private void OnFillBgChanged(object sender, RoutedEventArgs e)
        => BgFill.Fill = FillBg.IsChecked == true ? Brushes.White : Brushes.Transparent;

    // ── 확정 ────────────────────────────────────────────────
    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (Ink.Strokes.Count == 0 && BgImage.Source == null)
        {
            MessageBox.Show(this, "그린 내용이 없습니다.", "ThrowMe",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var bmp = RenderDrawArea();
        if (bmp == null || !SkinImageStore.SaveBitmap(_kind, bmp))
        {
            MessageBox.Show(this, "이미지를 저장하지 못했습니다. 로그를 확인하세요.", "ThrowMe",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Saved = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    /// <summary>DrawArea 를 OutputSize 정사각 PNG 비트맵으로 렌더한다(안내선·격자 제외).</summary>
    private BitmapSource? RenderDrawArea()
    {
        try
        {
            double w = DrawArea.ActualWidth, h = DrawArea.ActualHeight;
            if (w <= 0 || h <= 0) return null;

            // VisualBrush + 스케일: 화면 크기(360)가 아니라 OutputSize 해상도로 확대 렌더.
            double s = OutputSize / Math.Max(w, h);
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(s, s));
                dc.DrawRectangle(new VisualBrush(DrawArea), null, new Rect(new Size(w, h)));
                dc.Pop();
            }

            var rtb = new RenderTargetBitmap(OutputSize, OutputSize, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
        catch (Exception ex)
        {
            Logger.Error("Skin drawing render failed.", ex);
            return null;
        }
    }

    /// <summary>투명 영역이 보이도록 하는 체커보드 배경(그리기 보조용, 저장 제외).</summary>
    private static Brush MakeCheckerBrush()
    {
        var dg = new DrawingGroup();
        using (var dc = dg.Open())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x44)), null, new Rect(0, 0, 16, 16));
            var light = new SolidColorBrush(Color.FromRgb(0x46, 0x46, 0x52));
            dc.DrawRectangle(light, null, new Rect(0, 0, 8, 8));
            dc.DrawRectangle(light, null, new Rect(8, 8, 8, 8));
        }
        var brush = new DrawingBrush(dg)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 16, 16),
            ViewportUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }
}
