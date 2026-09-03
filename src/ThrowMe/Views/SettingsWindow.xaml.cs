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
        DwmChrome.AttachTo(this); // 둥근 모서리·그림자·테두리는 OS 가 그린다
        DataContext = settings;
        BuildThemeCards();
        BuildPetCards();
        UpdateCliCard();
        _slime.CliStatusChanged += (_, _) => UpdateCliCard();
        UpdateRebindText();
        UpdateAimKeyText();
        UpdateWindKeyText();
        UpdateInfiniteBounceLocks();
        BuildAutoMoveSection();
        UpdateAutoMoveLock();
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
        _slime.RoomStyleChanged += OnRoomStyleChanged;
        RefreshNetworkPanel();
        UpdateThemeLock();

        Closed += (_, _) =>
        {
            _settings.PropertyChanged -= OnSettingsPropertyChanged;
            _slime.RoomStateChanged -= OnRoomStateChanged;
            _slime.RoomStyleChanged -= OnRoomStyleChanged;
        };
    }

    private void OnSettingsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Skin):
                HighlightSelectedSkin();
                HighlightPetCards();
                UpdateBilliardSection();
                UpdateCustomImageSection();
                UpdateAutoMoveLock();
                break;
            case nameof(AppSettings.InfiniteBounce):
                UpdateInfiniteBounceLocks();
                break;
            case nameof(AppSettings.PetId):
                HighlightPetCards();
                break;
            case nameof(AppSettings.CliLinkEnabled):
                UpdateCliCard();
                break;
            case nameof(AppSettings.AutoMove):
                // 테마 변경·무한 튕기기로 밖에서 꺼진 경우 콤보 표시를 맞춘다.
                SyncAutoMoveBox();
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

    /// <summary>
    /// 무한 튕기기 중에는 감속과 반발력을 못 만지게 잠근다.
    ///
    /// 이 모드는 <b>감속 0 + 반발 100%</b> 를 전제로 한다. 둘 중 하나라도 슬라이더로 되돌리면
    /// 공이 결국 멈춰서 "무한 튕기기가 켜져 있는데 멈춘다"는 앞뒤가 안 맞는 상태가 된다.
    /// 끄면 켜기 직전 값으로 돌아가므로 여기서 막아도 잃는 것이 없다.
    ///
    /// 비활성화만 하면 왜 안 눌리는지 알 수 없으므로 설명 문구도 잠금 이유로 바꾼다.
    /// </summary>
    private void UpdateInfiniteBounceLocks()
    {
        bool locked = _settings.InfiniteBounce;

        SlowdownRow.IsEnabled = !locked;
        SlowdownRow.Opacity = locked ? 0.45 : 1.0;
        SlowdownDesc.ToolTip = locked
            ? "무한 튕기기가 켜져 있어 0 으로 고정됩니다."
            : "날아가던 공이 느려지는 정도입니다.";

        RestitutionRow.IsEnabled = !locked;
        RestitutionRow.Opacity = locked ? 0.45 : 1.0;
        RestitutionDesc.ToolTip = locked
            ? "무한 튕기기가 켜져 있어 100% 로 고정됩니다."
            : "벽에 튕기는 반발력.";
    }

    /// <summary>사이드바가 접혀 있는가(아이콘만 보이는 상태).</summary>
    private bool _sideCollapsed;

    /// <summary>
    /// 사이드바를 접고 편다. 접으면 글자를 숨기고 폭을 아이콘 크기로 줄여,
    /// 창을 좁게 써도 설정 내용이 눌리지 않게 한다.
    /// </summary>
    private void OnToggleSidebar(object sender, RoutedEventArgs e)
    {
        _sideCollapsed = !_sideCollapsed;
        SideCol.Width = new GridLength(_sideCollapsed ? 58 : 210);

        var vis = _sideCollapsed ? Visibility.Collapsed : Visibility.Visible;
        NavTextApp.Visibility = vis;
        NavText0.Visibility = vis;
        NavText1.Visibility = vis;
        NavText2.Visibility = vis;
        NavText3.Visibility = vis;
        NavText4.Visibility = vis;
        NavText5.Visibility = vis;
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

    /// <summary>한 페이지에 보여줄 릴리스 수.</summary>
    private const int NotesPerPage = 5;
    private readonly List<UpdateService.ReleaseNotes> _allNotes = new();
    private int _notesPage;   // 0-based

    private void OnRefreshNotes(object sender, RoutedEventArgs e) => _ = LoadReleaseNotesAsync(force: true);

    private void OnNotesPrev(object sender, RoutedEventArgs e)
    {
        if (_notesPage > 0) { _notesPage--; RenderNotesPage(); }
    }

    private void OnNotesNext(object sender, RoutedEventArgs e)
    {
        if ((_notesPage + 1) * NotesPerPage < _allNotes.Count) { _notesPage++; RenderNotesPage(); }
    }

    /// <summary>현재 페이지에 해당하는 릴리스만 그린다.</summary>
    private void RenderNotesPage()
    {
        NotesList.Items.Clear();

        int total = _allNotes.Count;
        int pages = Math.Max(1, (int)Math.Ceiling(total / (double)NotesPerPage));
        _notesPage = Math.Clamp(_notesPage, 0, pages - 1);

        string cur = UpdateService.Current.ToString(3);
        foreach (var r in _allNotes.Skip(_notesPage * NotesPerPage).Take(NotesPerPage))
            NotesList.Items.Add(BuildReleaseCard(r, isCurrent: r.Version == cur));

        NotesPager.Visibility = total > NotesPerPage ? Visibility.Visible : Visibility.Collapsed;
        NotesPageText.Text = $"{_notesPage + 1} / {pages}";
        NotesPrevBtn.IsEnabled = _notesPage > 0;
        NotesNextBtn.IsEnabled = _notesPage < pages - 1;

        // 페이지를 넘기면 목록 위로 올려 준다.
        NotesList.BringIntoView();
    }

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
            _allNotes.Clear();

            if (releases.Count == 0)
            {
                NotesPager.Visibility = Visibility.Collapsed;
                NotesStatusText.Text = "릴리스 정보를 가져오지 못했습니다. 네트워크를 확인해 주세요.";
                return;
            }

            _allNotes.AddRange(releases);
            _notesPage = 0;
            RenderNotesPage();

            _notesLoaded = true;
            string cur = UpdateService.Current.ToString(3);
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

    /// <summary>업데이트 카드의 버튼 → 업데이트 노트 탭으로 이동.</summary>
    private void OnGoToNotes(object sender, RoutedEventArgs e) => Nav.SelectedIndex = 5;

    private void OnResetPosition(object sender, RoutedEventArgs e) => _slime.ResetPositionPublic();

    /// <summary>
    /// 모든 설정을 기본값으로. 되돌릴 수 없으므로 먼저 확인을 받는다.
    /// 초기화 뒤에는 공 위치까지 맞춰 주고, 화면(설정창)도 새 값으로 다시 그린다.
    /// </summary>
    private void OnResetSettings(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            this,
            "모든 설정을 기본값으로 되돌립니다.\n\n" +
            "테마 · 물리(크기·반발·말랑함·던지기) · 소리 · 단축키 · 동작이 처음 상태가 됩니다.\n" +
            "방 코드와 비밀번호, 직접 그린 그림 파일은 지워지지 않습니다.\n\n" +
            "계속할까요?",
            "설정 초기화",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (answer != MessageBoxResult.OK) return;

        _settings.ResetToDefaults();
        _slime.ResetPositionPublic();   // 크기가 바뀌므로 공도 제자리로

        // 코드로 그려 둔 부분은 바인딩이 없어 따로 새로 그린다.
        BuildThemeCards();
        UpdateRebindText();
        UpdateAimKeyText();
        UpdateBilliardSection();
        UpdateCustomImageSection();

        MessageBox.Show(this, "설정을 기본값으로 되돌렸습니다.", "설정 초기화",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }


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
        (SlimeSkinKind.PaperPlane, "종이비행기"),
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

    private UserControl MakeSkin(SlimeSkinKind kind) => kind switch
    {
        SlimeSkinKind.Pet => MakePetPreview(),
        SlimeSkinKind.Billiard => new BilliardSkin(),
        SlimeSkinKind.Pokeball or SlimeSkinKind.Ultra or SlimeSkinKind.Master => new BallSkin(kind),
        SlimeSkinKind.Basketball => new BasketballSkin(),
        SlimeSkinKind.Bowling => new BowlingSkin(),
        SlimeSkinKind.PaperPlane => new PaperPlaneSkin(),
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
        => _settings.Skin == SlimeSkinKind.Pet ? CurrentPetName()
        : (Skins.FirstOrDefault(s => s.kind == _settings.Skin).name ?? _settings.Skin.ToString());

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
    /// <summary>
    /// 어떤 칸을 캡처 중인가. None 이면 캡처 중 아님.
    ///
    /// 잡기·숨기기는 <b>반드시 조합키</b>여야 해서 칸을 둘로 나눴다(수정자 + 키/클릭).
    /// 수정자 없이 단독 키를 허용하면 평소 타이핑과 클릭까지 전역으로 가로챈다 —
    /// 실제로 맨 좌클릭이 잡기로 걸려 공이 계속 끌려다닌 적이 있다.
    /// </summary>
    private enum HotkeyTarget { None, CatchMod, CatchKey, HideMod, HideKey, OpenSetHold, OpenSetKey }

    private HotkeyTarget _capturing = HotkeyTarget.None;
    private int _pMod, _pVk, _pMouse;            // 잡기 대기값
    private int _hMod, _hVk, _hMouse;            // 숨기기 대기값
    private int _oHold, _oVk;                    // 설정 열기 대기값(두 칸 모두 아무 키나)

    private void OnRebindOpenSetHold(object sender, RoutedEventArgs e)
        => BeginCapture(HotkeyTarget.OpenSetHold);

    private void OnRebindOpenSetKey(object sender, RoutedEventArgs e)
        => BeginCapture(HotkeyTarget.OpenSetKey);

    private void OnRebindCatchMod(object sender, RoutedEventArgs e)
        => BeginCapture(HotkeyTarget.CatchMod);

    private void OnRebindCatchKey(object sender, RoutedEventArgs e)
        => BeginCapture(HotkeyTarget.CatchKey);

    private void OnRebindHideMod(object sender, RoutedEventArgs e)
        => BeginCapture(HotkeyTarget.HideMod);

    private void OnRebindHideKey(object sender, RoutedEventArgs e)
        => BeginCapture(HotkeyTarget.HideKey);

    private void BeginCapture(HotkeyTarget target)
    {
        _capturing = target;
        bool isMod = target is HotkeyTarget.CatchMod or HotkeyTarget.HideMod;
        string prompt = isMod ? "수정자 입력…" : "키/클릭 입력…";

        // 캡처 중인 칸만 비운다 — 반대쪽 칸(이미 정해 둔 값)은 그대로 둔다.
        switch (target)
        {
            case HotkeyTarget.CatchMod: _pMod = 0; CatchModBtn.Content = prompt; break;
            case HotkeyTarget.CatchKey: _pVk = _pMouse = 0; CatchKeyBtn.Content = prompt; break;
            case HotkeyTarget.HideMod: _hMod = 0; HideModBtn.Content = prompt; break;
            case HotkeyTarget.HideKey: _hVk = _hMouse = 0; HideKeyBtn.Content = prompt; break;
            // 설정 열기는 두 칸 모두 아무 키나 받는다(수정자여도 되고 아니어도 된다).
            case HotkeyTarget.OpenSetHold: _oHold = 0; OpenSetHoldBtn.Content = "키 입력…"; break;
            case HotkeyTarget.OpenSetKey: _oVk = 0; OpenSetKeyBtn.Content = "키 입력…"; break;
        }
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

    // ── 종이비행기 바람 단축키(Ctrl 고정 + 키 1개, 즉시 적용) ──
    private bool _capturingWindKey;

    private void OnRebindWindKey(object sender, RoutedEventArgs e)
    {
        _capturingWindKey = true;
        WindKeyBtn.Content = "키 입력…";
    }

    private void UpdateWindKeyText()
        => WindKeyBtn.Content = _settings.WindHotkeyVk == 0
            ? "(없음)"
            : KeyDisplayName(_settings.WindHotkeyVk);

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturingWindKey)
        {
            Key wk = e.Key == Key.System ? e.SystemKey : e.Key;
            // 수정자는 Ctrl 고정이므로 수정자 단독 입력은 무시하고 계속 기다린다.
            if (wk is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;

            _settings.WindHotkeyVk = KeyInterop.VirtualKeyFromKey(wk);
            _capturingWindKey = false;
            UpdateWindKeyText();
            e.Handled = true;
            return;
        }

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

        if (_capturing == HotkeyTarget.None)
        {
            // 설정 초기화 단축키(Ctrl + R). 캡처 중일 때는 사용자가 그 조합을 단축키로
            // 지정하려는 것이므로 여기까지 오지 않는다(위 분기에서 이미 처리·반환).
            if ((e.Key == Key.R || e.SystemKey == Key.R)
                && Keyboard.Modifiers == ModifierKeys.Control)
            {
                OnResetSettings(this, new RoutedEventArgs());
                e.Handled = true;
            }
            return;
        }

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        bool isModifierKey = key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

        if (_capturing is HotkeyTarget.OpenSetHold or HotkeyTarget.OpenSetKey)
        {
            // 여기만 키보드의 모든 키를 받는다 — 수정자 단독(Ctrl 등)도 그대로 인정한다.
            int vkAny = KeyInterop.VirtualKeyFromKey(key);
            if (vkAny == 0) return;
            if (_capturing == HotkeyTarget.OpenSetHold) _oHold = vkAny; else _oVk = vkAny;
            EndCapture();
            e.Handled = true;
            return;
        }

        if (_capturing is HotkeyTarget.CatchMod or HotkeyTarget.HideMod)
        {
            // 수정자 칸: 수정자만 받는다. 두 개를 함께 누르면(Ctrl+Shift) 둘 다 들어간다.
            if (!isModifierKey) return;
            int mods = CurrentMods();
            if (mods == 0) return;
            if (_capturing == HotkeyTarget.CatchMod) _pMod = mods; else _hMod = mods;
        }
        else
        {
            // 키 칸: 수정자 단독은 무시하고 실제 키를 기다린다.
            if (isModifierKey) return;
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (_capturing == HotkeyTarget.CatchKey) { _pVk = vk; _pMouse = 0; }
            else { _hVk = vk; _hMouse = 0; }
        }

        EndCapture();
        e.Handled = true;
    }

    private void OnPreviewMouseDownCapture(object sender, MouseButtonEventArgs e)
    {
        // 마우스 클릭은 '키' 칸에서만 받는다(수정자 칸은 키보드 전용).
        if (_capturing is not (HotkeyTarget.CatchKey or HotkeyTarget.HideKey)) return;
        int btn = e.ChangedButton switch { MouseButton.Right => 2, MouseButton.Middle => 3, _ => 1 };
        if (_capturing == HotkeyTarget.CatchKey) { _pMouse = btn; _pVk = 0; }
        else { _hMouse = btn; _hVk = 0; }
        EndCapture();
        e.Handled = true;
    }

    private void EndCapture()
    {
        _capturing = HotkeyTarget.None;
        RefreshHotkeyBoxes();
        SaveBtn.IsEnabled = true; // 저장을 눌러야 적용
    }

    /// <summary>네 칸의 표시를 대기값으로 다시 그린다.</summary>
    private void RefreshHotkeyBoxes()
    {
        CatchModBtn.Content = ModText(_pMod);
        CatchKeyBtn.Content = KeyText(_pVk, _pMouse);
        HideModBtn.Content = ModText(_hMod);
        HideKeyBtn.Content = KeyText(_hVk, _hMouse);
        OpenSetHoldBtn.Content = _oHold != 0 ? HotkeyText.KeyName(_oHold) : "(키 필요)";
        OpenSetKeyBtn.Content = _oVk != 0 ? HotkeyText.KeyName(_oVk) : "(키 필요)";
    }

    // ── 자동 이동 ───────────────────────────────────────────
    /// <summary>콤보 상자를 채우는 동안 SelectionChanged 로 설정이 덮어써지지 않게 막는다.</summary>
    private bool _fillingAutoMove;

    /// <summary>모니터 목록과 현재 선택을 채운다.</summary>
    private void BuildAutoMoveSection()
    {
        _fillingAutoMove = true;
        try
        {
            AutoMoveBox.SelectedIndex = (int)_settings.AutoMove;
            CursorStyleBox.SelectedIndex = (int)_settings.CursorFollowStyle;

            AutoMoveMonitorBox.Items.Clear();
            AutoMoveMonitorBox.Items.Add(new ComboBoxItem { Content = "전체 화면", Tag = "" });

            var bounds = _slime.MonitorBoundsForSettings;
            for (int i = 0; i < bounds.Count; i++)
            {
                var b = bounds[i];
                string key = SlimeWindow.MonitorKey(b);
                AutoMoveMonitorBox.Items.Add(new ComboBoxItem
                {
                    Content = $"모니터 {i + 1} — {(int)b.Width}x{(int)b.Height}",
                    Tag = key,
                });
            }

            int sel = 0;
            for (int i = 0; i < AutoMoveMonitorBox.Items.Count; i++)
                if (((ComboBoxItem)AutoMoveMonitorBox.Items[i]).Tag as string == _settings.AutoMoveMonitor)
                { sel = i; break; }
            AutoMoveMonitorBox.SelectedIndex = sel;
        }
        finally { _fillingAutoMove = false; }
    }

    /// <summary>자동 이동은 슬라임(젤리) 테마 전용. 다른 테마에서는 카드를 잠그고 이유를 보여 준다.</summary>
    private void UpdateAutoMoveLock()
    {
        bool locked = _settings.Skin != SlimeSkinKind.Jelly;
        AutoMoveLockedNotice.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        AutoMoveRows.IsEnabled = !locked;
        AutoMoveRows.Opacity = locked ? 0.45 : 1.0;
    }

    /// <summary>설정값이 밖에서 바뀌었을 때 콤보 상자 표시를 맞춘다(핸들러가 다시 쓰지 않게 막고).</summary>
    private void SyncAutoMoveBox()
    {
        if (AutoMoveBox.SelectedIndex == (int)_settings.AutoMove) return;
        _fillingAutoMove = true;
        try { AutoMoveBox.SelectedIndex = (int)_settings.AutoMove; }
        finally { _fillingAutoMove = false; }
    }

    private void OnAutoMoveChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingAutoMove) return;
        int i = AutoMoveBox.SelectedIndex;
        if (i >= 0) _settings.AutoMove = (AutoMoveMode)i;
    }

    private void OnCursorStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingAutoMove) return;
        int i = CursorStyleBox.SelectedIndex;
        if (i >= 0) _settings.CursorFollowStyle = (CursorFollowStyle)i;
    }

    private void OnAutoMoveMonitorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingAutoMove) return;
        if (AutoMoveMonitorBox.SelectedItem is ComboBoxItem it)
            _settings.AutoMoveMonitor = it.Tag as string ?? "";
    }

    /// <summary>수정자 가상키 → 수정자 비트. 수정자가 아니면 0.</summary>
    private static int ModBitOf(int vk) => vk switch
    {
        0x11 or 0xA2 or 0xA3 => 2, // Ctrl
        0x10 or 0xA0 or 0xA1 => 4, // Shift
        0x12 or 0xA4 or 0xA5 => 1, // Alt
        0x5B or 0x5C => 8,         // Win
        _ => 0,
    };

    /// <summary>조합키로 성립하는가 — 수정자와 키(또는 클릭)가 모두 있어야 한다.</summary>
    private static bool IsValidCombo(int mod, int vk, int mouse) => mod != 0 && (vk != 0 || mouse != 0);

    private void OnSaveHotkey(object sender, RoutedEventArgs e)
    {
        if (!IsValidCombo(_pMod, _pVk, _pMouse) || !IsValidCombo(_hMod, _hVk, _hMouse))
        {
            MessageBox.Show(this,
                "잡기와 숨기기는 수정자와 키를 모두 지정해야 합니다.\n\n" +
                "비어 있는 칸을 눌러 Ctrl·Shift·Alt·Win 중 하나와, 함께 쓸 키나 마우스 클릭을 넣어 주세요.",
                "단축키", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_oHold == 0 || _oVk == 0)
        {
            MessageBox.Show(this,
                "설정 열기 단축키는 두 칸을 모두 채워야 합니다.\n\n" +
                "앞 칸의 키를 누르고 있는 동안 뒤 칸의 키를 누르면 설정이 열립니다.",
                "단축키", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_oHold == _oVk)
        {
            MessageBox.Show(this,
                "설정 열기 단축키의 두 칸에 같은 키를 넣을 수 없습니다.",
                "단축키", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 잡기·숨기기와 겹치는지 본다. 앞 칸이 수정자면 같은 조합이 되어 서로 잡아먹는다.
        int oMod = ModBitOf(_oHold);
        if (oMod != 0 && ((oMod == _pMod && _oVk == _pVk) || (oMod == _hMod && _oVk == _hVk)))
        {
            MessageBox.Show(this,
                "설정 열기 단축키가 잡기 또는 숨기기와 같습니다.\n\n같은 조합은 쓸 수 없습니다.",
                "단축키", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.CatchHotkeyMod = _pMod;
        _settings.CatchHotkeyVk = _pVk;
        _settings.CatchHotkeyMouse = _pMouse;
        _settings.HideHotkeyMod = _hMod;
        _settings.HideHotkeyVk = _hVk;
        _settings.HideHotkeyMouse = _hMouse;
        _settings.OpenSettingsHoldVk = _oHold;
        _settings.OpenSettingsVk = _oVk;
        SaveBtn.IsEnabled = false;
    }

    private void UpdateRebindText()
    {
        // 대기값을 현재 설정으로 초기화 — 한쪽만 바꿔 저장해도 다른 쪽이 지워지지 않도록.
        _pMod = _settings.CatchHotkeyMod; _pVk = _settings.CatchHotkeyVk; _pMouse = _settings.CatchHotkeyMouse;
        _hMod = _settings.HideHotkeyMod; _hVk = _settings.HideHotkeyVk; _hMouse = _settings.HideHotkeyMouse;
        _oHold = _settings.OpenSettingsHoldVk; _oVk = _settings.OpenSettingsVk;
        RefreshHotkeyBoxes();
    }

    /// <summary>수정자 칸 표시. 비어 있으면 넣어야 한다는 걸 드러낸다.</summary>
    private static string ModText(int mod)
    {
        string s = HotkeyText.Mod(mod);
        return s.Length > 0 ? s : "(수정자 필요)";
    }

    /// <summary>키/클릭 칸 표시.</summary>
    private static string KeyText(int vk, int mouse)
    {
        string s = HotkeyText.Key(vk, mouse);
        return s.Length > 0 ? s : "(키 필요)";
    }

    /// <summary>Key.Oem3 처럼 알아보기 어려운 이름을 실제 새겨진 글자로 바꿔 보여준다.</summary>
    private static string KeyDisplayName(int vk) => HotkeyText.KeyName(vk);

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

    private void OnRoomStateChanged() => Dispatcher.Invoke(() =>
    {
        RefreshPartyList();
        UpdateThemeLock();
    });

    private void OnRoomStyleChanged() => Dispatcher.Invoke(() =>
    {
        if (RoomThemePanel.Visibility == Visibility.Visible) RefreshRoomStyleSummary();
    });

    /// <summary>
    /// 방에 들어가 있고 내가 방장이 아니면 테마 탭을 막는다.
    /// 방장의 테마·가중치·그림이 그대로 내려오므로 여기서 바꿔 봐야 곧 덮어써진다.
    /// </summary>
    private void UpdateThemeLock()
    {
        bool inRoom = _slime.RelayAuth.Enabled && _slime.RoomNodes.Count > 0;
        bool locked = inRoom && !_slime.IsHost;

        ThemeLockedNotice.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        ThemeCards.IsEnabled = !locked;
        ThemeCards.Opacity = locked ? 0.45 : 1.0;
        CustomImageSection.IsEnabled = !locked;
        CustomImageSection.Opacity = locked ? 0.45 : 1.0;
    }

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
        // 파티 목록을 그리다 실패해도 설정창 자체는 열려야 한다.
        // (예전에 여기서 형 변환 예외가 나면서 생성자가 통째로 터져 설정창이 아예 안 열렸다.)
        try { RefreshPartyListCore(); }
        catch (Exception ex)
        {
            Logger.Error("RefreshPartyList failed; showing fallback.", ex);
            try
            {
                PartyList?.Items.Clear();
                if (PartyHint != null) PartyHint.Text = "파티원 목록을 표시하지 못했습니다.";
            }
            catch { /* 여기서 더 실패해도 창은 열어야 한다 */ }
        }
    }

    private void RefreshPartyListCore()
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

        // 이미 방장인데 '입장' 탭이 남아 있으면 헷갈린다(내 방에 다시 입장하는 것처럼 보임).
        // 방장일 때는 감추고 '방 생성' 쪽으로 고정한다.
        TabJoin.Visibility = host ? Visibility.Collapsed : Visibility.Visible;
        if (host && TabJoin.IsChecked == true)
        {
            TabCreate.IsChecked = true; // Checked 이벤트가 패널 전환까지 처리
        }

        // 방 겉모습 요약은 방에 있는 모두에게 보여 준다(참가자도 방장이 뭘 골랐는지 알 수 있게).
        RoomThemePanel.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (RoomThemePanel.Visibility == Visibility.Visible) RefreshRoomStyleSummary();


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


    // ── 방 겉모습 요약 ──────────────────────────────────────
    private static string SkinLabel(string? name)
    {
        if (Enum.TryParse<SlimeSkinKind>(name, ignoreCase: true, out var k))
        {
            foreach (var (kind, label) in Skins) if (kind == k) return label;
        }
        return name ?? "-";
    }

    /// <summary>방장이 정한 테마·가중치·그림을 한눈에 보이게 정리한다(모두에게 표시).</summary>
    private void RefreshRoomStyleSummary()
    {
        RoomStyleSummary.Children.Clear();

        bool host = _slime.IsHost;
        RoomStyleHint.Text = host
            ? "'테마' 탭에서 고른 값이 방에 있는 모든 PC에 그대로 적용됩니다."
            : $"방장({_slime.RoomHost})이 정한 설정이 적용되어 있습니다. 참가자는 바꿀 수 없습니다.";

        var s = _slime.CurrentRoomStyle;
        if (s == null)
        {
            RoomStyleSummary.Children.Add(new TextBlock
            {
                Text = host ? "테마를 고르면 여기에 표시됩니다." : "방장이 설정을 보내면 표시됩니다.",
                Style = (Style)FindResource("RowDesc"),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        // 테마 이름 + 그림 여부
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        head.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("Accent"),
            Padding = new Thickness(9, 3, 9, 3),
            Child = new TextBlock
            {
                Text = SkinLabel(s.Skin),
                Foreground = Brushes.White,
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
            },
        });
        if (!string.IsNullOrEmpty(s.ImageSkin) && s.SkinImageEnabled)
        {
            head.Children.Add(new TextBlock
            {
                Text = $"그림 적용 ({s.SkinImageScale * 100:0}%)",
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            });
        }
        RoomStyleSummary.Children.Add(head);

        AddRow("던지기 가중치", $"{s.ThrowPower:0.0}x");
        AddRow("반발력", $"{s.Restitution:P0}");
        AddRow("말랑함", $"{s.Softness:P0}");
        AddRow("크기", $"{s.SlimeSize:0}px");

        void AddRow(string name, string value)
        {
            var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var l = new TextBlock
            {
                Text = name,
                Foreground = (Brush)FindResource("MutedBrush"),
                FontSize = 12,
            };
            var v = new TextBlock
            {
                Text = value,
                Foreground = (Brush)FindResource("TextBrush"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
            };
            Grid.SetColumn(l, 0);
            Grid.SetColumn(v, 1);
            g.Children.Add(l);
            g.Children.Add(v);
            RoomStyleSummary.Children.Add(g);
        }
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
            // DarkSeparator 는 Brush 가 아니라 Style 이다. 여기에 캐스팅하면
            // InvalidCastException 이 나면서 파티 목록이 통째로 안 그려졌다(그리고 그 예외가
            // ApplyRoomState 를 타고 올라가 소유권 동기화까지 막았다).
            BorderBrush = (Brush)FindResource(isSelf ? "Accent" : "TrackBg"),
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
