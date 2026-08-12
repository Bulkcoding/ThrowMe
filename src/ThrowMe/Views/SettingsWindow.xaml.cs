using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ThrowMe.Models;
using ThrowMe.Network;
using ThrowMe.Services;
using ThrowMe.Views.Skins;
using Application = System.Windows.Application;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Brushes = System.Windows.Media.Brushes;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Image = System.Windows.Controls.Image;
using MessageBox = System.Windows.MessageBox;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using DragDropEffects = System.Windows.DragDropEffects;
using DataFormats = System.Windows.DataFormats;
using DragDrop = System.Windows.DragDrop;
using VerticalAlignment = System.Windows.VerticalAlignment;
using UserControl = System.Windows.Controls.UserControl;
using TextBox = System.Windows.Controls.TextBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;

namespace ThrowMe.Views;

/// <summary>
/// 다크 2-pane 설정 UI(Clawd 스타일). 좌측 네비 + 우측 패널. DataContext = AppSettings 직접 바인딩.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SlimeWindow _slime;
    private readonly Dictionary<SlimeSkinKind, Border> _themeCards = new();
    private readonly Dictionary<SlimeSkinKind, Border> _themePreviewHosts = new();

    public SettingsWindow(AppSettings settings, SlimeWindow slime)
    {
        _settings = settings;
        _slime = slime;
        InitializeComponent();
        DataContext = settings;
        BuildThemeCards();
        UpdateRebindText();
        UpdateAimKeyText();
        UpdateBilliardSection();
        UpdateCustomImageSection();
        _settings.PropertyChanged += OnSettingsPropertyChanged;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseDown += OnPreviewMouseDownCapture;

        // 업데이트 안내에 현재 버전 표시(버전별 내용은 '업데이트 노트' 탭에서).
        UpdateInfoText.Text =
            $"현재 v{UpdateService.Current.ToString(3)} · 앱을 켤 때 새 버전이 있으면 " +
            "자동으로 받아 적용하고 다시 시작합니다.";

        _slime.RelayStateChanged += st => Dispatcher.Invoke(() => UpdateNetStatus(st));
        // 방 멤버·순서·방장이 바뀌면 파티 목록을 다시 그린다.
        _slime.RoomStateChanged += OnRoomStateChanged;
        RefreshNetworkPanel();

        Closed += (_, _) =>
        {
            _settings.PropertyChanged -= OnSettingsPropertyChanged;
            _slime.RoomStateChanged -= OnRoomStateChanged;
        };
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Skin):
                HighlightSelectedSkin();
                UpdateBilliardSection();
                UpdateCustomImageSection();
                break;
            case nameof(AppSettings.SkinImages):
            case nameof(AppSettings.SkinImageEnabled):
            case nameof(AppSettings.SkinImageScale):
                RefreshThemePreviews();
                UpdateCustomImageSection();
                break;
        }
    }

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Hide();

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PanelGeneral == null) return; // InitializeComponent 도중 초기 선택 이벤트 무시
        int i = Nav.SelectedIndex;
        PanelGeneral.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelTheme.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelSound.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;
        PanelShortcuts.Visibility = i == 3 ? Visibility.Visible : Visibility.Collapsed;
        PanelNetwork.Visibility = i == 4 ? Visibility.Visible : Visibility.Collapsed;
        PanelUpdateNotes.Visibility = i == 5 ? Visibility.Visible : Visibility.Collapsed;
        if (i == 4) RefreshNetworkPanel();
        if (i == 5) _ = LoadReleaseNotesAsync(force: false);
    }

    // ── 업데이트 노트 탭 ────────────────────────────────────
    private bool _notesLoaded;
    private bool _notesLoading;

    private void OnRefreshNotes(object sender, RoutedEventArgs e) => _ = LoadReleaseNotesAsync(force: true);

    /// <summary>릴리스 목록을 받아 버전별로 펼쳐 보여준다. 한 번 받아 두면 다시 받지 않는다.</summary>
    private async Task LoadReleaseNotesAsync(bool force)
    {
        if (_notesLoading) return;
        if (_notesLoaded && !force) return;

        _notesLoading = true;
        NotesRefreshBtn.IsEnabled = false;
        NotesStatusText.Text = "불러오는 중…";

        try
        {
            var releases = await UpdateService.FetchAllReleasesAsync();
            NotesList.Items.Clear();

            if (releases.Count == 0)
            {
                NotesStatusText.Text = "릴리스 정보를 가져오지 못했습니다. 네트워크를 확인해 주세요.";
                return;
            }

            string cur = UpdateService.Current.ToString(3);
            foreach (var r in releases)
                NotesList.Items.Add(BuildReleaseCard(r, isCurrent: r.Version == cur));

            _notesLoaded = true;
            NotesStatusText.Text = $"{releases.Count}개 버전";
            NotesHeaderText.Text = $"현재 v{cur} · 버전별로 무엇이 바뀌었는지 볼 수 있습니다.";
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load release notes list.", ex);
            NotesStatusText.Text = "불러오지 못했습니다.";
        }
        finally
        {
            _notesLoading = false;
            NotesRefreshBtn.IsEnabled = true;
        }
    }

    /// <summary>릴리스 한 건 = 버전 배지 + 제목 + 본문(마크다운 최소 서식).</summary>
    private Border BuildReleaseCard(UpdateService.ReleaseNotes r, bool isCurrent)
    {
        var stack = new StackPanel();

        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource(isCurrent ? "Accent" : "TrackBg"),
            Padding = new Thickness(9, 3, 9, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "v" + r.Version,
                Foreground = isCurrent ? Brushes.White : (Brush)FindResource("TextBrush"),
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
            },
        });
        if (isCurrent)
        {
            head.Children.Add(new TextBlock
            {
                Text = "사용 중",
                Foreground = (Brush)FindResource("Accent"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            });
        }
        stack.Children.Add(head);

        if (!string.IsNullOrWhiteSpace(r.Title) && r.Title != "v" + r.Version)
        {
            stack.Children.Add(new TextBlock
            {
                Text = r.Title,
                Foreground = (Brush)FindResource("TextBrush"),
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }

        // 본문은 릴리스 노트 팝업과 같은 렌더러를 써서 서식을 맞춘다.
        var body = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        NotesRenderer.Render(body, r.Body, this);
        stack.Children.Add(body);

        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = (Brush)FindResource("CardBg"),
            BorderBrush = isCurrent ? (Brush)FindResource("Accent") : Brushes.Transparent,
            BorderThickness = new Thickness(isCurrent ? 1 : 0),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack,
        };
    }

    private void OnResetPosition(object sender, RoutedEventArgs e) => _slime.ResetPositionPublic();


    private void UpdateBilliardSection()
        => BilliardSection.Visibility = _settings.Skin == SlimeSkinKind.Billiard ? Visibility.Visible : Visibility.Collapsed;

    // ── 테마 카드(스킨 미리보기) ─────────────────────────────
    private static readonly (SlimeSkinKind kind, string name)[] Skins =
    {
        (SlimeSkinKind.Jelly, "슬라임"),
        (SlimeSkinKind.Billiard, "당구공"),
        (SlimeSkinKind.Pokeball, "몬스터볼"),
        (SlimeSkinKind.Ultra, "하이퍼볼"),
        (SlimeSkinKind.Master, "마스터볼"),
        (SlimeSkinKind.Basketball, "농구공"),
        (SlimeSkinKind.Bowling, "볼링공"),
    };

    private void BuildThemeCards()
    {
        foreach (var (kind, name) in Skins)
        {
            var previewHost = new Border
            {
                Width = 96,
                Height = 82,
                CornerRadius = new CornerRadius(8),
                Background = (Brush)FindResource("WinBg"),
                Child = MakePreviewVisual(kind, 74),
            };
            _themePreviewHosts[kind] = previewHost;

            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(previewHost);
            stack.Children.Add(new TextBlock
            {
                Text = name,
                Foreground = (Brush)FindResource("TextBrush"),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 6, 0, 0),
            });

            var card = new Border
            {
                Width = 116,
                Padding = new Thickness(8, 10, 8, 10),
                Margin = new Thickness(0, 0, 12, 12),
                CornerRadius = new CornerRadius(11),
                Background = (Brush)FindResource("CardBg"),
                BorderThickness = new Thickness(2),
                BorderBrush = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = stack,
                Tag = kind,
            };
            card.MouseLeftButtonUp += (_, _) => _settings.Skin = kind;
            _themeCards[kind] = card;
            ThemeCards.Children.Add(card);
        }
        HighlightSelectedSkin();
    }

    private static UserControl MakeSkin(SlimeSkinKind kind) => kind switch
    {
        SlimeSkinKind.Billiard => new BilliardSkin(),
        SlimeSkinKind.Pokeball or SlimeSkinKind.Ultra or SlimeSkinKind.Master => new BallSkin(kind),
        SlimeSkinKind.Basketball => new BasketballSkin(),
        SlimeSkinKind.Bowling => new BowlingSkin(),
        _ => new JellySkin(),
    };

    /// <summary>커스텀 이미지가 있으면 겹쳐 보여주는 미리보기(실제 공과 같은 구성).
    /// SlimeWindow 의 덧씌우기 레이어와 같은 96 디자인 좌표·원형 클립을 쓴다.</summary>
    private FrameworkElement MakePreviewVisual(SlimeSkinKind kind, double size)
    {
        var design = new Grid { Width = 96, Height = 96 };
        design.Children.Add(MakeSkin(kind));

        var img = _settings.SkinImageEnabled && SkinImageStore.Supports(kind)
            ? SkinImageStore.Load(kind)
            : null;
        if (img != null)
        {
            double d = 84.0 * System.Math.Clamp(_settings.SkinImageScale, 0.2, 2.0);
            var layer = new Grid
            {
                Clip = new EllipseGeometry(new Point(48, 48), 42, 42),
                IsHitTestVisible = false,
            };
            layer.Children.Add(new Image
            {
                Source = img,
                Width = d,
                Height = d,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            layer.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Margin = new Thickness(6),
                Fill = SphereShadeBrush(),
            });
            design.Children.Add(layer);
        }

        return new Viewbox { Width = size, Height = size, Stretch = Stretch.Uniform, Child = design };
    }

    /// <summary>덧씌운 이미지가 스티커가 아니라 공 표면처럼 보이게 하는 가장자리 음영.</summary>
    private static Brush SphereShadeBrush()
    {
        var b = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.5, 0.5),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5,
        };
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 0.62));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x38, 0, 0, 0), 0.88));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x6E, 0, 0, 0), 1.0));
        b.Freeze();
        return b;
    }

    private void RefreshThemePreviews()
    {
        foreach (var (kind, host) in _themePreviewHosts)
            host.Child = MakePreviewVisual(kind, 74);
    }

    private void HighlightSelectedSkin()
    {
        var accent = (Brush)FindResource("Accent");
        foreach (var (kind, card) in _themeCards)
            card.BorderBrush = kind == _settings.Skin ? accent : Brushes.Transparent;
    }

    // ── 테마별 커스텀 이미지 ────────────────────────────────
    /// <summary>현재 선택 테마 이름(설정창 표시용).</summary>
    private string CurrentThemeName()
        => Skins.FirstOrDefault(s => s.kind == _settings.Skin).name ?? _settings.Skin.ToString();

    private void UpdateCustomImageSection()
    {
        var kind = _settings.Skin;
        bool supported = SkinImageStore.Supports(kind);
        CustomImageSection.Visibility = supported ? Visibility.Visible : Visibility.Collapsed;
        if (!supported) return;

        CustomImageHeader.Text = $"커스텀 이미지 — {CurrentThemeName()}";

        bool has = SkinImageStore.Has(kind);
        _settings.SkinImages.TryGetValue(kind.ToString(), out string? name);
        CustomImageName.Text = has
            ? (string.IsNullOrWhiteSpace(name) ? "(직접 그린 이미지)" : name)
            : "(없음)";
        RemoveImageBtn.IsEnabled = has;

        CustomPreviewHost.Children.Clear();
        CustomPreviewHost.Children.Add(MakePreviewVisual(kind, 84));
    }

    /// <summary>SkinImages 기록을 갱신하고 화면·저장에 반영한다.</summary>
    private void SetSkinImageRecord(SlimeSkinKind kind, string? displayName)
    {
        string key = kind.ToString();
        if (displayName == null) _settings.SkinImages.Remove(key);
        else _settings.SkinImages[key] = displayName;
        _settings.NotifySkinImagesChanged(); // 공·미리보기 갱신 + 디바운스 자동 저장
    }

    private void OnLoadSkinImage(object sender, RoutedEventArgs e)
    {
        var kind = _settings.Skin;
        var dlg = new OpenFileDialog
        {
            Title = $"{CurrentThemeName()} 에 씌울 이미지 선택",
            Filter = "이미지 파일|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|모든 파일|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        if (!SkinImageStore.Import(kind, dlg.FileName))
        {
            MessageBox.Show(this, "이미지를 불러오지 못했습니다. 다른 파일로 시도해 보세요.", "ThrowMe",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SetSkinImageRecord(kind, System.IO.Path.GetFileName(dlg.FileName));
    }

    private void OnDrawSkinImage(object sender, RoutedEventArgs e)
    {
        var kind = _settings.Skin;
        var win = new SkinDrawWindow(kind, CurrentThemeName()) { Owner = this };
        win.ShowDialog();
        // 빈 문자열 = 이미지는 있으나 원본 파일이 없음(직접 그린 것)
        if (win.Saved) SetSkinImageRecord(kind, "");
    }

    private void OnRemoveSkinImage(object sender, RoutedEventArgs e)
    {
        var kind = _settings.Skin;
        SkinImageStore.Remove(kind);
        SetSkinImageRecord(kind, null);
    }

    // ── 단축키 재설정(변경→캡처→저장) ───────────────────────
    /// <summary>어떤 단축키를 캡처 중인가. None 이면 캡처 중 아님.</summary>
    private enum HotkeyTarget { None, Catch, Hide }

    private HotkeyTarget _capturing = HotkeyTarget.None;
    private int _pMod, _pVk, _pMouse;            // 잡기 대기값
    private int _hMod, _hVk, _hMouse;            // 숨기기 대기값

    private void OnRebindCatch(object sender, RoutedEventArgs e)
    {
        _capturing = HotkeyTarget.Catch;
        _pMod = _pVk = _pMouse = 0;
        RebindBtn.Content = "키/클릭 입력…";
        SaveBtn.IsEnabled = false;
    }

    private void OnRebindHide(object sender, RoutedEventArgs e)
    {
        _capturing = HotkeyTarget.Hide;
        _hMod = _hVk = _hMouse = 0;
        RebindHideBtn.Content = "키/클릭 입력…";
        SaveBtn.IsEnabled = false;
    }

    private static int CurrentMods()
    {
        var m = Keyboard.Modifiers;
        return (m.HasFlag(ModifierKeys.Alt) ? 1 : 0)
             | (m.HasFlag(ModifierKeys.Control) ? 2 : 0)
             | (m.HasFlag(ModifierKeys.Shift) ? 4 : 0)
             | (m.HasFlag(ModifierKeys.Windows) ? 8 : 0);
    }

    // ── 농구공 조준 단축키(단일 키, 즉시 적용) ───────────────
    private bool _capturingAimKey;

    private void OnRebindAimKey(object sender, RoutedEventArgs e)
    {
        _capturingAimKey = true;
        AimKeyBtn.Content = "키 입력…";
    }

    private void UpdateAimKeyText()
        => AimKeyBtn.Content = _settings.BasketballAimVk == 0
            ? "(없음)"
            : KeyInterop.KeyFromVirtualKey(_settings.BasketballAimVk).ToString();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingAimKey)
        {
            Key k = e.Key == Key.System ? e.SystemKey : e.Key;
            // 좌/우 수정자는 공용 VK 로 저장(Shift/Ctrl/Alt 를 좌우 구분 없이 인식)
            int aimVk = k switch
            {
                Key.LeftShift or Key.RightShift => 0x10,
                Key.LeftCtrl or Key.RightCtrl => 0x11,
                Key.LeftAlt or Key.RightAlt => 0x12,
                _ => KeyInterop.VirtualKeyFromKey(k),
            };
            _settings.BasketballAimVk = aimVk;
            _capturingAimKey = false;
            UpdateAimKeyText();
            e.Handled = true;
            return;
        }

        if (_capturing == HotkeyTarget.None) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return; // 수정자 단독은 대기

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (_capturing == HotkeyTarget.Catch) { _pMod = CurrentMods(); _pVk = vk; _pMouse = 0; }
        else { _hMod = CurrentMods(); _hVk = vk; _hMouse = 0; }
        EndCapture();
        e.Handled = true;
    }

    private void OnPreviewMouseDownCapture(object sender, MouseButtonEventArgs e)
    {
        if (_capturing == HotkeyTarget.None) return;
        int btn = e.ChangedButton switch { MouseButton.Right => 2, MouseButton.Middle => 3, _ => 1 };
        if (_capturing == HotkeyTarget.Catch) { _pMod = CurrentMods(); _pMouse = btn; _pVk = 0; }
        else { _hMod = CurrentMods(); _hMouse = btn; _hVk = 0; }
        EndCapture();
        e.Handled = true;
    }

    private void EndCapture()
    {
        if (_capturing == HotkeyTarget.Catch) RebindBtn.Content = HotkeyText(_pMod, _pVk, _pMouse);
        else RebindHideBtn.Content = HotkeyText(_hMod, _hVk, _hMouse);
        _capturing = HotkeyTarget.None;
        SaveBtn.IsEnabled = true; // 저장을 눌러야 적용
    }

    private void OnSaveHotkey(object sender, RoutedEventArgs e)
    {
        _settings.CatchHotkeyMod = _pMod;
        _settings.CatchHotkeyVk = _pVk;
        _settings.CatchHotkeyMouse = _pMouse;
        _settings.HideHotkeyMod = _hMod;
        _settings.HideHotkeyVk = _hVk;
        _settings.HideHotkeyMouse = _hMouse;
        SaveBtn.IsEnabled = false;
    }

    private void UpdateRebindText()
    {
        // 대기값을 현재 설정으로 초기화 — 한쪽만 바꿔 저장해도 다른 쪽이 지워지지 않도록.
        _pMod = _settings.CatchHotkeyMod; _pVk = _settings.CatchHotkeyVk; _pMouse = _settings.CatchHotkeyMouse;
        _hMod = _settings.HideHotkeyMod; _hVk = _settings.HideHotkeyVk; _hMouse = _settings.HideHotkeyMouse;

        RebindBtn.Content = HotkeyText(_pMod, _pVk, _pMouse);
        RebindHideBtn.Content = HotkeyText(_hMod, _hVk, _hMouse);
    }

    private static string HotkeyText(int mod, int vk, int mouse)
    {
        var parts = new List<string>();
        if ((mod & 2) != 0) parts.Add("Ctrl");
        if ((mod & 4) != 0) parts.Add("Shift");
        if ((mod & 1) != 0) parts.Add("Alt");
        if ((mod & 8) != 0) parts.Add("Win");
        if (vk != 0) parts.Add(KeyDisplayName(vk));
        else if (mouse == 1) parts.Add("좌클릭");
        else if (mouse == 2) parts.Add("우클릭");
        else if (mouse == 3) parts.Add("중간클릭");
        return parts.Count > 0 ? string.Join(" + ", parts) : "(없음)";
    }

    /// <summary>Key.Oem3 처럼 알아보기 어려운 이름을 실제 새겨진 글자로 바꿔 보여준다.</summary>
    private static string KeyDisplayName(int vk) => vk switch
    {
        0xC0 => "`",   // Oem3 (물결/백틱)
        0xBD => "-",   // OemMinus
        0xBB => "=",   // OemPlus
        0xDB => "[",   // Oem4
        0xDD => "]",   // Oem6
        0xDC => "\\",  // Oem5
        0xBA => ";",   // Oem1
        0xDE => "'",   // Oem7
        0xBC => ",",   // OemComma
        0xBE => ".",   // OemPeriod
        0xBF => "/",   // Oem2
        _ => KeyInterop.KeyFromVirtualKey(vk).ToString(),
    };

    // ── 멀티 PC(릴레이) 설정 ────────────────────────────────
    // 배치는 "파티 순서(좌 → 우)" 하나로만 정한다(위/아래 없음). 방장이 드래그로 순서를
    // 바꾸면 PartyLayout 이 좌우 체인 링크로 변환해 방 전체에 배포한다.

    private void RefreshNetworkPanel()
    {
        var a = _slime.RelayAuth;
        NetEnabled.IsChecked = a.Enabled;
        // 서버 주소는 내장값 사용 → 표시만(입력 불가). 개발용으로 덮어썼을 때만 그 값이 보인다.
        bool custom = !string.Equals(a.EffectiveServerBaseUrl, AuthService.DefaultServerBaseUrl,
                                     StringComparison.OrdinalIgnoreCase);
        NetServerInfo.Text = custom
            ? $"서버: {a.EffectiveServerBaseUrl} (개발용 오버라이드)"
            : "서버: 기본 릴레이 서버에 자동 연결됩니다.";

        // 이미 방에 들어가 있으면 그 값을 각 탭에 채워 둔다.
        CreateRoom.Text = a.RoomCode;
        JoinRoom.Text = a.RoomCode;
        CreateSecret.Password = a.Secret;
        JoinSecret.Password = a.Secret;
        CreateNode.Text = a.NodeId;
        JoinNode.Text = a.NodeId;

        RefreshPartyList();
        UpdateNetStatus(_slime.RelayState);
    }

    private void OnRoomStateChanged() => Dispatcher.Invoke(RefreshPartyList);

    private void OnRoomTabChanged(object sender, RoutedEventArgs e)
    {
        if (PaneCreate == null || PaneJoin == null) return;
        bool create = TabCreate.IsChecked == true;
        PaneCreate.Visibility = create ? Visibility.Visible : Visibility.Collapsed;
        PaneJoin.Visibility = create ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>혼동하기 쉬운 문자(0/O, 1/I) 를 뺀 방 코드 생성.</summary>
    private void OnGenerateCode(object sender, RoutedEventArgs e)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
        var buf = new byte[4];
        rng.GetBytes(buf);
        var sb = new System.Text.StringBuilder("SLIME-");
        foreach (byte b in buf) sb.Append(chars[b % chars.Length]);
        CreateRoom.Text = sb.ToString();
    }

    private void OnCreateRoom(object sender, RoutedEventArgs e)
        => ConnectRoom(CreateRoom.Text, CreateSecret.Password, CreateNode.Text);

    private void OnJoinRoom(object sender, RoutedEventArgs e)
        => ConnectRoom(JoinRoom.Text, JoinSecret.Password, JoinNode.Text);

    /// <summary>방 코드/비밀번호/이름으로 접속. 방을 처음 만든 PC가 서버에서 방장이 된다.</summary>
    private void ConnectRoom(string room, string secret, string node)
    {
        room = (room ?? "").Trim();
        secret = secret ?? "";
        node = (node ?? "").Trim();

        if (room.Length == 0 || secret.Length == 0 || node.Length == 0)
        {
            PartyHint.Text = "방 코드·비밀번호·PC 이름을 모두 입력하세요.";
            return;
        }

        var a = _slime.RelayAuth;
        a.Enabled = true;
        NetEnabled.IsChecked = true;
        a.RoomCode = room;
        a.Secret = secret;
        a.NodeId = node;
        _slime.ApplyRelaySettings(); // 저장 + 재연결
        UpdateNetStatus(_slime.RelayState);
    }

    private void OnLeaveRoom(object sender, RoutedEventArgs e)
    {
        var a = _slime.RelayAuth;
        a.Enabled = false;
        NetEnabled.IsChecked = false;
        _slime.ApplyRelaySettings();
        RefreshPartyList();
        UpdateNetStatus(_slime.RelayState);
    }

    // ── 파티원 목록 (드래그로 좌 → 우 순서 변경) ───────────────
    private readonly List<string> _partyOrder = new();

    private void RefreshPartyList()
    {
        if (PartyList == null) return;

        var nodes = _slime.RoomNodes;
        var order = _slime.RoomOrder.ToList();
        // 순서에 없는 접속자는 뒤에 붙이고, 접속 안 한 사람은 목록에서 뺀다.
        var online = nodes.Select(n => n.NodeId).ToHashSet(StringComparer.Ordinal);
        var shown = order.Where(online.Contains).ToList();
        foreach (var id in online) if (!shown.Contains(id)) shown.Add(id);

        _partyOrder.Clear();
        _partyOrder.AddRange(shown);

        bool host = _slime.IsHost;
        NetLeaveBtn.Visibility = _slime.RelayAuth.Enabled ? Visibility.Visible : Visibility.Collapsed;

        // 방 공통 테마는 방장에게만 노출.
        RoomThemePanel.Visibility = host && shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (RoomThemePanel.Visibility == Visibility.Visible) SyncRoomThemeCombo();

        PartyHint.Text = shown.Count == 0
            ? "방에 입장하면 참여자가 표시됩니다."
            : host
                ? "카드를 드래그해 순서를 바꾸세요. 왼쪽이 실제 화면 왼쪽입니다. (우클릭 → 방장 위임)"
                : "배치는 방장이 정합니다.";

        // 배치를 화면처럼 보이게: 카드(모니터) 를 좌 → 우 로 늘어놓고 사이를 선으로 잇는다.
        var chain = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < shown.Count; i++)
        {
            if (i > 0) chain.Children.Add(BuildConnector());
            chain.Children.Add(BuildPartyCard(shown[i], i, host));
        }

        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = chain,
        };

        PartyList.Items.Clear();
        PartyList.Items.Add(scroll);

        PartyChainInfo.Text = shown.Count > 1
            ? "공이 카드 오른쪽 끝을 넘으면 다음 카드의 왼쪽에서 나옵니다. 양 끝은 벽에 튕깁니다."
            : shown.Count == 1 ? "혼자 있는 방입니다. 다른 PC가 입장하면 좌우로 이어집니다." : "";
    }

    // ── 방 공통 테마(방장) ──────────────────────────────────
    private bool _syncingRoomTheme;

    private void SyncRoomThemeCombo()
    {
        if (RoomThemeCombo.Items.Count == 0)
        {
            foreach (var (kind, name) in Skins)
                RoomThemeCombo.Items.Add(new ComboBoxItem { Content = name, Tag = kind });
        }

        _syncingRoomTheme = true;
        foreach (ComboBoxItem it in RoomThemeCombo.Items)
        {
            if (it.Tag is SlimeSkinKind k && k == _settings.Skin) { RoomThemeCombo.SelectedItem = it; break; }
        }
        _syncingRoomTheme = false;
    }

    private void OnRoomThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingRoomTheme) return;
        if (RoomThemeCombo.SelectedItem is not ComboBoxItem { Tag: SlimeSkinKind kind }) return;
        _settings.Skin = kind;        // 내 테마 먼저 적용
        _slime.PushRoomTheme(kind);   // 방 전체에 배포(방장만 유효)
    }

    /// <summary>카드 사이를 잇는 연결선(공이 지나가는 통로).</summary>
    private UIElement BuildConnector()
    {
        var panel = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 2, 0),
        };
        panel.Children.Add(new TextBlock
        {
            Text = "↔",
            Foreground = (Brush)FindResource("Accent"),
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        panel.Children.Add(new Border
        {
            Height = 2, Width = 26,
            Background = (Brush)FindResource("Accent"),
            Opacity = 0.55,
            CornerRadius = new CornerRadius(1),
        });
        return panel;
    }

    /// <summary>파티원 한 명 = 모니터 한 대를 나타내는 카드.</summary>
    private Border BuildPartyCard(string nodeId, int index, bool hostCanDrag)
    {
        bool isSelf = nodeId == _slime.SelfNodeId;
        bool isHost = nodeId == _slime.RoomHost;
        bool hasBall = _slime.RoomNodes.FirstOrDefault(n => n.NodeId == nodeId)?.HasBall == true;

        var stack = new StackPanel { Margin = new Thickness(10, 8, 10, 8) };

        // 상단 배지 줄: 순번 · 방장 · 공
        var badges = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        badges.Children.Add(new TextBlock
        {
            Text = $"{index + 1}",
            Foreground = (Brush)FindResource("MutedBrush"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (isHost) badges.Children.Add(Badge("방장", (Brush)FindResource("Accent")));
        if (hasBall) badges.Children.Add(Badge("● 공", (Brush)FindResource("TextBrush")));
        stack.Children.Add(badges);

        // 화면 모양(모니터) — 공이 있으면 안에 점을 찍는다.
        var screen = new Border
        {
            Width = 92, Height = 54,
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("WinBg"),
            BorderBrush = (Brush)FindResource(isSelf ? "Accent" : "DarkSeparator"),
            BorderThickness = new Thickness(isSelf ? 2 : 1),
        };
        if (hasBall)
        {
            screen.Child = new System.Windows.Shapes.Ellipse
            {
                Width = 16, Height = 16,
                Fill = (Brush)FindResource("Accent"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        stack.Children.Add(screen);

        stack.Children.Add(new TextBlock
        {
            Text = nodeId + (isSelf ? " (나)" : ""),
            Foreground = (Brush)FindResource("TextBrush"),
            FontWeight = isSelf ? FontWeights.SemiBold : FontWeights.Normal,
            FontSize = 12,
            MaxWidth = 96,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0),
        });

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = (Brush)FindResource("CardBg"),
            Child = stack,
            Tag = nodeId,
            AllowDrop = hostCanDrag,
            Cursor = hostCanDrag ? Cursors.SizeAll : null,
        };

        // 우클릭 → 방장 위임(방장만, 자기 자신 제외)
        if (_slime.IsHost && !isSelf)
        {
            var menu = new ContextMenu { Style = TryFindResource("DarkMenu") as Style };
            var item = new MenuItem
            {
                Header = $"{nodeId} 에게 방장 위임",
                Style = TryFindResource("DarkMenuItem") as Style,
            };
            item.Click += (_, _) => _slime.TransferHost(nodeId);
            menu.Items.Add(item);
            card.ContextMenu = menu;
        }

        if (hostCanDrag)
        {
            card.PreviewMouseLeftButtonDown += (s, _) =>
            {
                if (s is Border b && b.Tag is string id)
                    DragDrop.DoDragDrop(b, id, DragDropEffects.Move);
            };
            card.Drop += OnPartyRowDrop;
            card.DragOver += (_, ev) =>
            {
                ev.Effects = ev.Data.GetDataPresent(DataFormats.StringFormat) ? DragDropEffects.Move : DragDropEffects.None;
                ev.Handled = true;
            };
        }
        return card;

        TextBlock Badge(string text, Brush brush) => new()
        {
            Text = text,
            Foreground = brush,
            FontSize = 11,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private void OnPartyRowDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (!_slime.IsHost) return;
        if (sender is not Border target || target.Tag is not string targetId) return;
        if (e.Data.GetData(DataFormats.StringFormat) is not string draggedId) return;
        if (draggedId == targetId) return;

        int from = _partyOrder.IndexOf(draggedId);
        int to = _partyOrder.IndexOf(targetId);
        if (from < 0 || to < 0) return;

        _partyOrder.RemoveAt(from);
        _partyOrder.Insert(to, draggedId);

        // 서버에 순서 알림 + 좌우 체인 배치 배포(방장 권한).
        _slime.PushPartyOrder(_partyOrder.ToList());
        RefreshPartyList();
        e.Handled = true;
    }

    private void UpdateNetStatus(RelayState st)
    {
        if (NetStatus == null) return;
        NetStatus.Text = st switch
        {
            RelayState.Connected => "연결됨 ✓",
            RelayState.Connecting => "연결 중…",
            RelayState.Reconnecting => "재연결 중…",
            RelayState.Failed => "연결 실패 — 서버 주소를 확인하세요",
            _ => "꺼짐",
        };
        NetStatus.Foreground = (Brush)FindResource(st == RelayState.Connected ? "TextBrush" : "MutedBrush");
    }

}
