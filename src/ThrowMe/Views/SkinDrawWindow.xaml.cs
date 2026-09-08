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
using Image = System.Windows.Controls.Image;
using Button = System.Windows.Controls.Button;
using VerticalAlignment = System.Windows.VerticalAlignment;

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

    private static readonly Color[] PaletteColors = Hex(
        // 회색조 6
        "000000", "333333", "666666", "999999", "CCCCCC", "FFFFFF",
        // 선명 9
        "E53935", "FB8C00", "FDD835", "43A047", "00ACC1", "1E88E5", "8E24AA", "EC407A", "795548",
        // 밝은 9
        "EF9A9A", "FFCC80", "FFF59D", "A5D6A7", "80DEEA", "90CAF9", "CE93D8", "F8BBD0", "BCAAA4",
        // 어두운 9
        "B71C1C", "E65100", "F9A825", "1B5E20", "006064", "0D47A1", "4A148C", "880E4F", "3E2723",
        // 보조 3
        "FF5722", "CDDC39", "607D8B");

    private static Color[] Hex(params string[] hex) => hex.Select(ParseHex).ToArray();

    private static Color ParseHex(string h)
    {
        int v = int.Parse(h, System.Globalization.NumberStyles.HexNumber);
        return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    private static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private readonly SlimeSkinKind _kind;
    private readonly List<Border> _swatches = new();
    private Color _color = PaletteColors[0];
    private bool _erasing;

    private readonly AppSettings _settings;
    private readonly List<Border> _recentSwatches = new();
    /// <summary>배경이 채워져 있으면 그 색. null 이면 투명.</summary>
    private Color? _bgColor;
    private const int MaxRecent = 8;

    private readonly string _themeName;
    private bool _sideOpen;
    private const double ClosedWidth = 470, OpenWidth = 660, SideWidth = 190;

    /// <summary>적용을 눌러 저장까지 끝났는가.</summary>
    public bool Saved { get; private set; }

    public SkinDrawWindow(SlimeSkinKind kind, string themeName, AppSettings settings)
    {
        _kind = kind;
        _themeName = themeName;
        _settings = settings;
        InitializeComponent();
        DwmChrome.AttachTo(this); // 설정창과 같은 둥근 모서리·그림자·테두리

        TitleText.Text = $"{themeName} — 공에 그리기";
        Checker.Fill = MakeCheckerBrush();
        BuildPalette();
        BuildRecent();
        ApplyBrush();
        UpdateBackgroundUi();

        // 이미 커스텀 이미지가 있으면 배경으로 깔아 그 위에 이어 그릴 수 있게 한다.
        BgImage.Source = SkinImageStore.Load(kind);
        BuildTemplateList();
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
                Width = 26,
                Height = 26,
                Margin = new Thickness(0, 6, 6, 0),
                CornerRadius = new CornerRadius(13),
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
        foreach (var s in _recentSwatches)
            s.BorderBrush = !_erasing && (Color)s.Tag! == _color ? accent : Brushes.Transparent;
    }

    // ── 최근 색 / 임의 색 ───────────────────────────────────
    private void BuildRecent()
    {
        Recent.Children.Clear();
        _recentSwatches.Clear();
        foreach (string hex in _settings.DrawRecentColors.Take(MaxRecent))
        {
            Color c;
            try { c = ParseHex(hex.TrimStart('#')); } catch { continue; }
            var swatch = new Border
            {
                Width = 26, Height = 26,
                Margin = new Thickness(0, 4, 6, 0),
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(c),
                BorderThickness = new Thickness(2.5),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = c,
            };
            swatch.MouseLeftButtonUp += (_, _) => { _color = c; _erasing = false; ApplyBrush(); };
            _recentSwatches.Add(swatch);
            Recent.Children.Add(swatch);
        }
        if (_recentSwatches.Count == 0)
            Recent.Children.Add(new TextBlock { Text = "(임의 색을 고르면 여기에 남습니다)", Style = (Style)FindResource("RowDesc"), Margin = new Thickness(0, 4, 0, 0) });
    }

    private void PushRecent(Color c)
    {
        string hex = ToHex(c);
        var list = _settings.DrawRecentColors;
        list.RemoveAll(x => string.Equals(x, hex, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, hex);
        while (list.Count > MaxRecent) list.RemoveAt(list.Count - 1);
        _settings.NotifyDrawRecentColorsChanged();
        BuildRecent();
    }

    private void OnOpenCustomColor(object sender, RoutedEventArgs e)
    {
        Picker.SetColor(_color);
        ColorPopup.IsOpen = true;
    }

    /// <summary>선택기에서 색이 바뀔 때마다 붓에 바로 반영(미리 그려 볼 수 있게). 최근 칸에는 '이 색으로' 를 눌러야 들어간다.</summary>
    private void OnPickerColorChanged(object? sender, Color c)
    {
        _color = c;
        _erasing = false;
        ApplyBrush();
    }

    private void OnUseCustomColor(object sender, RoutedEventArgs e)
    {
        _color = Picker.Color;
        _erasing = false;
        ApplyBrush();
        PushRecent(_color);
        ColorPopup.IsOpen = false;
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
        _bgColor = null;       // 배경색도 함께 없앤다
        UpdateBackgroundUi();
    }

    // ── 배경 ────────────────────────────────────────────────
    /// <summary>누른 순간의 붓 색으로 배경을 채운다. 이후 붓 색을 바꿔도 배경은 그대로다.</summary>
    private void OnFillBackground(object sender, RoutedEventArgs e)
    {
        _bgColor = _color;
        UpdateBackgroundUi();
    }

    private void OnClearBackground(object sender, RoutedEventArgs e)
    {
        _bgColor = null;
        UpdateBackgroundUi();
    }

    private void UpdateBackgroundUi()
    {
        BgFill.Fill = _bgColor is { } c ? new SolidColorBrush(c) : Brushes.Transparent;
        BgSwatch.Background = _bgColor is { } c2 ? new SolidColorBrush(c2) : Brushes.Transparent;
        ClearBgBtn.IsEnabled = _bgColor != null;
        BgDesc.Text = _bgColor is { } c3
            ? $"배경 {ToHex(c3)}. 다시 채우면 지금 붓 색으로 바뀌고, 지우면 투명이 됩니다."
            : "배경 없음(투명). 채우기를 누르면 지금 붓 색으로 깔립니다.";
    }

    /// <summary>선·바탕 그림·배경이 모두 없는가(불러오기 확인, 저장 가능 판정에 쓴다).</summary>
    private bool IsCanvasEmpty() => Ink.Strokes.Count == 0 && BgImage.Source == null && _bgColor == null;

    // ── 확정 ────────────────────────────────────────────────
    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (IsCanvasEmpty())
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

        // 공에 적용한 그림은 템플릿 목록에도 자동 보관한다(직전 자동 항목과 같으면 건너뜀). 보관 실패는 적용을 막지 않는다.
        DrawTemplateStore.AddAuto(bmp, _themeName);

        Saved = true;
        Close();
    }

    // ── 템플릿 사이드 탭 ────────────────────────────────────
    private void OnToggleSide(object sender, RoutedEventArgs e)
    {
        _sideOpen = !_sideOpen;
        SideCol.Width = new GridLength(_sideOpen ? SideWidth : 0);
        Width = _sideOpen ? OpenWidth : ClosedWidth;
        SideToggle.Content = _sideOpen ? "◂ 템플릿" : "템플릿 ▸";
        if (_sideOpen) BuildTemplateList();
    }

    private void BuildTemplateList()
    {
        TemplateList.Children.Clear();
        var items = DrawTemplateStore.List();
        if (items.Count == 0)
        {
            TemplateList.Children.Add(new TextBlock
            {
                Text = "보관된 템플릿이 없습니다.\n공에 적용하면 자동으로 보관되고, 위에서 이름을 붙여 저장할 수도 있어요.",
                Style = (Style)FindResource("RowDesc"), TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (var t in items)
        {
            var thumb = new Border
            {
                Width = 64, Height = 64, CornerRadius = new CornerRadius(8),
                Background = MakeCheckerBrush(),
                Child = new Image { Source = DrawTemplateStore.Load(t.Id, 128), Stretch = Stretch.Uniform },
            };
            var name = new TextBlock
            {
                Text = t.Name, Foreground = (Brush)FindResource("TextBrush"), FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = t.Name,
            };
            var meta = new TextBlock
            {
                Text = t.Auto ? $"{t.Theme} · 자동" : t.Theme,
                Style = (Style)FindResource("RowDesc"), Margin = new Thickness(0, 2, 0, 0),
            };
            var texts = new StackPanel { Margin = new Thickness(8, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(name);
            texts.Children.Add(meta);

            var remove = new Button
            {
                Content = "✕", Width = 24, Height = 24, FontSize = 11, Cursor = Cursors.Hand,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = (Brush)FindResource("MutedBrush"), VerticalAlignment = VerticalAlignment.Top,
                ToolTip = "삭제",
            };
            string id = t.Id;
            remove.Click += (_, ev) => { ev.Handled = true; DrawTemplateStore.Remove(id); BuildTemplateList(); };

            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(thumb, 0); Grid.SetColumn(texts, 1); Grid.SetColumn(remove, 2);
            row.Children.Add(thumb); row.Children.Add(texts); row.Children.Add(remove);

            var card = new Border
            {
                Background = (Brush)FindResource("CardBg"), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6), Cursor = Cursors.Hand, Child = row, Margin = new Thickness(0, 0, 0, 6),
            };
            card.MouseLeftButtonUp += (_, _) => LoadTemplate(id);
            TemplateList.Children.Add(card);
        }
    }

    /// <summary>템플릿을 캔버스에 깐다. 선·바탕 그림·배경을 모두 지우고 템플릿만 남긴다(그린 것이 있으면 확인).</summary>
    private void LoadTemplate(string id)
    {
        if (!IsCanvasEmpty())
        {
            var r = MessageBox.Show(this, "지금 그린 내용을 지우고 이 템플릿으로 바꿀까요?", "ThrowMe",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
        }
        var img = DrawTemplateStore.Load(id);
        if (img == null)
        {
            MessageBox.Show(this, "템플릿 파일을 읽지 못했습니다. 목록에서 지워 주세요.", "ThrowMe",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Ink.Strokes.Clear();
        _bgColor = null;
        UpdateBackgroundUi();
        BgImage.Source = img;
    }

    private void OnSaveAsTemplate(object sender, RoutedEventArgs e)
    {
        if (!_sideOpen) OnToggleSide(sender, e);
        TemplateNameBox.Focus();
        TemplateNameBox.SelectAll();
    }

    private void OnTemplateNameKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { OnSaveTemplateNamed(sender, e); e.Handled = true; }
    }

    private void OnSaveTemplateNamed(object sender, RoutedEventArgs e)
    {
        if (IsCanvasEmpty())
        {
            MessageBox.Show(this, "저장할 그림이 없습니다.", "ThrowMe", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var bmp = RenderDrawArea();
        if (bmp == null || DrawTemplateStore.AddNamed(bmp, TemplateNameBox.Text, _themeName) == null)
        {
            MessageBox.Show(this, "템플릿을 저장하지 못했습니다. 로그를 확인하세요.", "ThrowMe", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        TemplateNameBox.Text = "";
        BuildTemplateList();
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
