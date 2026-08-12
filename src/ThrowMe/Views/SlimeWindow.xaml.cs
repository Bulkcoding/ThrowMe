using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using ThrowMe.Animation;
using ThrowMe.Effects;
using ThrowMe.Models;
using ThrowMe.Network;
using ThrowMe.Physics;
using ThrowMe.Services;
using ThrowMe.Views.Skins;
using WinFormsCursor = System.Windows.Forms.Cursor;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using Application = System.Windows.Application;
using Ellipse = System.Windows.Shapes.Ellipse;
using Rectangle = System.Windows.Shapes.Rectangle;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;
using Canvas = System.Windows.Controls.Canvas;

namespace ThrowMe.Views;

/// <summary>
/// 투명 슬라임 표시 + 마우스 입력 수집 + 렌더 루프(물리·애니메이션 tick).
/// 좌표 처리: 물리는 물리 스크린 픽셀, 창 배치 시 DPI 배율로 DIP 변환.
/// </summary>
public partial class SlimeWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MonitorLayoutService _monitors;
    private readonly SlimePhysicsEngine _physics;
    private readonly ThrowInputTracker _tracker;
    private SlimeAnimationController _animation = null!;

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;

    private bool _isDragging;
    private Vector2 _dragOffset;          // 커서 - 슬라임 위치 (물리 px)
    private Vector2 _pressCursor;         // 버튼 누른 순간 커서 (클릭/드래그 판정)
    private double _grabbedSpeed;         // 잡는 순간의 속도(px/s) — 낚아채기/펀치 판정
    private bool _ballWasOpen;            // 잡는 순간 볼이 열려 있었는가(클릭 토글 기준)

    // 스핀 이펙트 요소(코드 생성)
    private readonly List<Ellipse> _sparkles = new();
    private readonly List<double> _sparklePhase = new();

    // 스핀: 드래그 곡선으로 각속도 충전
    private double _dragSpin;             // 충전 중 각속도(deg/s)
    private Vector2 _prevDragDir;         // 직전 드래그 진행 방향
    private Vector2 _lastDragCursor;      // 직전 스핀 샘플 커서
    private double _lastDragTime;         // 직전 스핀 샘플 시각

    private bool _renderingActive;
    private double _lastFrameTime;

    // 표정 상태 (요청: 속도/충돌에 따른 표정 변경)
    private SlimeExpression _expression = SlimeExpression.Normal;
    private double _dizzyUntil;                        // 이 시각(초)까지 Dizzy 유지
    private const double FlyingSpeedFraction = 0.28;   // ImpactReferenceSpeed 대비 이 이상이면 Flying
    private const double DizzyDurationSeconds = 0.9;    // 강한 충돌 후 Dizzy 지속
    private const double DizzyImpactFraction = 0.55;    // ImpactReferenceSpeed 대비 이 이상 충돌이면 Dizzy

    // Phase 4: 타격감(효과음·파티클). 파티클 렌더는 전 모니터 오버레이가 담당.
    private readonly AudioService _audio;
    private readonly ParticleSystem _particles;
    private ParticleOverlayWindow? _overlay;

    // 타격 문구("Hit!" 등, 메이플식) — 젤리 스킨 전용
    private readonly HitTextSystem _hitText;
    private HitTextOverlayWindow? _hitTextOverlay;

    // ── 멀티 PC 인터넷 확장(릴레이) ─────────────────────────
    // 설정(방 코드/시크릿/서버)이 있을 때만 활성. 없으면 순수 로컬 토이로 동작(기존과 동일).
    private AuthService _auth = null!;
    private RelayClient? _relay;
    private BallHandoffCoordinator? _coord;
    private NetworkedWalkableArea? _netArea;
    private bool _networked;
    private string _selfNodeId = "";
    private bool _connected;      // 서버 WSS 연결됨
    private bool _ownsBall = true; // 이 PC가 공을 소유(표시)하는가. 서버 프레즌스로 갱신.

    /// <summary>릴레이 연결 상태 변화(설정창 표시용).</summary>
    public event Action<RelayState>? RelayStateChanged;

    public SlimeWindow(AppSettings settings, MonitorLayoutService monitors)
    {
        _settings = settings;
        _monitors = monitors;

        InitializeComponent();

        Topmost = _settings.AlwaysOnTop;
        ShowInTaskbar = _settings.ShowInTaskbar;
        _settings.PropertyChanged += OnSettingsChanged;

        _tracker = new ThrowInputTracker(_settings);
        // 충돌 판정은 MonitorLayoutService(IWalkableArea)에 위임 → 멀티 모니터 대응.
        _physics = new SlimePhysicsEngine(_settings, _monitors);

        ApplySkin();      // 스킨 적용 시 UpdateSkinBehavior 가 _physics 를 참조하므로 그 뒤에 호출
        BuildSpinFx();
        // 릴레이 설정이 있으면 네트워크 확장 활성화(연결된 엣지 통과 허용 + 핸드오프 조정자).
        _auth = AuthService.Load();
        if (_auth.IsConfigured) SetupNetworking();

        _audio = new AudioService(_settings);
        _particles = new ParticleSystem(_settings);
        _hitText = new HitTextSystem();

        // 입력 이벤트
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;

        _monitors.LayoutChanged += OnMonitorLayoutChanged;
    }

    private double Now => _clock.Elapsed.TotalSeconds;

    // ── 초기화 ──────────────────────────────────────────────
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UpdateDpiScale();
        ApplyWindowSize();
        ResetPositionToCenter();
        ApplyWindowPosition();

        // 파티클/효과 렌더용 클릭 통과 오버레이(전 모니터). 입력을 막지 않는다.
        _overlay = new ParticleOverlayWindow(_settings, _monitors);
        _overlay.Show();

        _hitTextOverlay = new HitTextOverlayWindow(_monitors);
        _hitTextOverlay.Show();

        // 전역 잡기 단축키 등록
        _hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);
        RegisterHotkeys();

        // 릴레이 서버 연결 시작(설정돼 있을 때만). NAT/방화벽 무관(아웃바운드 WSS).
        _relay?.Start();

        // 시작은 정지 상태이므로 렌더 루프를 돌리지 않는다(CPU 절감).
    }

    // ── 전역 단축키(잡기 / 빠르게 숨기기) ───────────────────
    private const int WM_HOTKEY = 0x0312;
    private const int CatchHotkeyId = 0xB001;
    private const int HideHotkeyId = 0xB002;
    private const uint MOD_NOREPEAT = 0x4000;
    private IntPtr _hwnd;
    private HwndSource? _hwndSource;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ── 마우스 버튼 트리거(수정자+클릭) : 전역 저수준 마우스 훅 ──
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201, WM_RBUTTONDOWN = 0x0204, WM_MBUTTONDOWN = 0x0207;
    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);
    private LowLevelMouseProc? _mouseProc;
    private IntPtr _mouseHook;

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, LowLevelMouseProc cb, IntPtr hMod, uint thread);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr h, int code, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);

    private void RegisterHotkeys()
    {
        if (_hwnd == IntPtr.Zero) return;
        // 키보드 트리거
        UnregisterHotKey(_hwnd, CatchHotkeyId);
        if (_settings.CatchHotkeyVk != 0)
            RegisterHotKey(_hwnd, CatchHotkeyId, (uint)_settings.CatchHotkeyMod | MOD_NOREPEAT, (uint)_settings.CatchHotkeyVk);

        UnregisterHotKey(_hwnd, HideHotkeyId);
        if (_settings.HideHotkeyVk != 0)
            RegisterHotKey(_hwnd, HideHotkeyId, (uint)_settings.HideHotkeyMod | MOD_NOREPEAT, (uint)_settings.HideHotkeyVk);

        // 마우스 트리거 — 둘 중 하나라도 쓰면 훅 하나로 함께 처리
        RemoveMouseTrigger();
        if (_settings.CatchHotkeyMouse != 0 || _settings.HideHotkeyMouse != 0)
        {
            _mouseProc = MouseHookProc;
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, GetModuleHandle(null), 0);
        }
    }

    private void RemoveMouseTrigger()
    {
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        _mouseProc = null;
    }

    private static bool ModifiersHeld(int m)
    {
        bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        if ((m & 2) != 0 && !Down(0x11)) return false; // Ctrl
        if ((m & 4) != 0 && !Down(0x10)) return false; // Shift
        if ((m & 1) != 0 && !Down(0x12)) return false; // Alt
        if ((m & 8) != 0 && !(Down(0x5B) || Down(0x5C))) return false; // Win
        return true;
    }

    private static int MouseMsgFor(int button) => button switch
    {
        2 => WM_RBUTTONDOWN,
        3 => WM_MBUTTONDOWN,
        _ => WM_LBUTTONDOWN,
    };

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            // 숨기기를 먼저 본다: 숨겨진 상태에선 잡기가 의미 없으므로 우선순위를 준다.
            if (_settings.HideHotkeyMouse != 0
                && msg == MouseMsgFor(_settings.HideHotkeyMouse)
                && ModifiersHeld(_settings.HideHotkeyMod))
            {
                try { ToggleQuickHide(); } catch { }
                return (IntPtr)1; // 삼킴(다른 창으로 전달 안 함)
            }
            if (_settings.CatchHotkeyMouse != 0
                && msg == MouseMsgFor(_settings.CatchHotkeyMouse)
                && ModifiersHeld(_settings.CatchHotkeyMod))
            {
                try { CatchToCursor(); } catch { }
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case CatchHotkeyId: CatchToCursor(); handled = true; break;
                case HideHotkeyId: ToggleQuickHide(); handled = true; break;
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>빠르게 숨기기: 슬라임과 부수 요소(당구공·이펙트 오버레이)를 한 번에 감추거나 되돌린다.</summary>
    private void ToggleQuickHide() => _settings.SlimeVisible = !_settings.SlimeVisible;

    /// <summary>SlimeVisible 에 맞춰 부수 창들까지 함께 감춘다(숨기기가 "완전히" 사라지게).</summary>
    private void ApplyVisibility()
    {
        bool show = _settings.SlimeVisible;
        if (show && _ownsBall) Show();
        else if (!show) Hide();

        // 테마가 띄운 것은 전부 함께 감춘다(규칙 §3.6) — 슬라임만 사라지면 숨긴 티가 남는다.
        // Close() 가 아니라 Hide() 로만 감춰서, 다시 보이기 하면 직전 상태 그대로 복원된다.
        foreach (var b in _extraBalls)                       // 당구 3/4구
        {
            try { if (show) b.Show(); else b.Hide(); } catch { }
        }
        foreach (var h in _hoops)                            // 농구골대·백보드·그물
        {
            try { if (show) h.Show(); else h.Hide(); } catch { }
        }
        foreach (var p in _pins)                             // 볼링핀(쓰러진 상태 유지)
        {
            try { if (show) p.Show(); else p.Hide(); } catch { }
        }
        try { if (show) _lane?.Show(); else _lane?.Hide(); } catch { } // 레인·거터·점수판

        if (show) { _overlay?.Show(); _hitTextOverlay?.Show(); }
        else { _overlay?.Hide(); _hitTextOverlay?.Hide(); }

        if (show) EnsureRendering();
    }

    /// <summary>단축키: 슬라임을 마우스 커서 위치로 데려와 정지(잡힘). 빠르게 날아가도 즉시 회수.</summary>
    private void CatchToCursor()
    {
        if (!IsVisible) { _settings.SlimeVisible = true; }
        Vector2 cursor = CursorPhysical();
        double half = _settings.SlimeSize / 2.0;
        _isDragging = false;
        _hasPrevBallY = false; // 커서로 회수(순간이동) → 득점 오검출 방지
        _physics.Velocity = Vector2.Zero;
        _physics.AngularVelocity = 0;
        _physics.SurfaceSpin = 0;
        _dragSpin = 0;
        _physics.SetPositionClamped(cursor - new Vector2(half, half));
        if (BowlingOn) { _ballGutterSide = 0; ClampBallForPlacement(); }
        _animation.OnImpact(_settings.ImpactReferenceSpeed * 0.25); // 잡히는 작은 반응
        ApplyWindowPosition();
        EnsureRendering();
    }

    private void UpdateDpiScale()
    {
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget != null)
        {
            Matrix m = src.CompositionTarget.TransformToDevice; // DIP → device(px)
            _dpiScaleX = m.M11 > 0 ? m.M11 : 1.0;
            _dpiScaleY = m.M22 > 0 ? m.M22 : 1.0;
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _dpiScaleX = newDpi.DpiScaleX > 0 ? newDpi.DpiScaleX : 1.0;
        _dpiScaleY = newDpi.DpiScaleY > 0 ? newDpi.DpiScaleY : 1.0;
        ApplyWindowSize();
        InvalidateWindowPositionCache(); // WPF 가 창을 옮겼을 수 있으니 캐시 무효화
        ApplyWindowPosition();
    }

    // 창은 슬라임의 4배(양쪽 1.5*SlimeSize 패딩). 넓은 클릭 영역 → 빠른 슬라임도 근처 클릭으로 낚아채기.
    private double EffectPadPx => _settings.SlimeSize * 1.5;

    private void ApplyWindowSize()
    {
        double s = _settings.SlimeSize;
        // 창(넓은 잡기 영역) + 스핀 이펙트 박스(2.5배, 중앙 고정) + 중앙 슬라임 크기.
        Width = 4.0 * s / _dpiScaleX;
        Height = 4.0 * s / _dpiScaleY;
        SpinFxBox.Width = 2.5 * s / _dpiScaleX;
        SpinFxBox.Height = 2.5 * s / _dpiScaleY;
        SlimeBox.Width = s / _dpiScaleX;
        SlimeBox.Height = s / _dpiScaleY;
        // 클릭 영역은 공 + 여유만. 창 전체를 받으면 날아가는 동안 다른 창 클릭을 삼킨다.
        // 설정창을 만지는 동안에는 여유를 없애 공 그림만큼만 받는다.
        double margin = SettingsOpen ? 0 : Math.Max(0, _settings.ClickMarginPx);
        double hit = s + 2.0 * margin;
        HitArea.Width = hit / _dpiScaleX;
        HitArea.Height = hit / _dpiScaleY;
        // 스핀 조준 원(공의 66% 크기)
        SpinAim.Width = 0.66 * s / _dpiScaleX;
        SpinAim.Height = 0.66 * s / _dpiScaleY;
        SpinDotShift.X = _spinOffset.X * (SpinAim.Width / 2.0);
        SpinDotShift.Y = _spinOffset.Y * (SpinAim.Height / 2.0);

        // 애니메이션 컨트롤러는 XAML Transform 이 준비된 뒤 1회 생성.
        _animation ??= new SlimeAnimationController(SlimeScale, SlimeRotate, _settings);
        UpdateSkinBehavior();
    }

    /// <summary>농구공 중력 가속도(px/s^2). 부드러운 낙하.</summary>
    private const double BasketballGravity = 2200.0;

    /// <summary>농구공 던지기 감쇠(마우스 속도 대비). 살살 던져지도록.</summary>
    private const double BasketballThrowScale = 0.55;
    /// <summary>농구공 던지기 속도 상한(px/s).</summary>
    private const double BasketballMaxThrow = 3300.0;
    /// <summary>조준 모드에서 당긴 거리(px)당 발사 속도.</summary>
    private const double BasketballAimScale = 7.0;

    /// <summary>스킨별 물리/애니메이션 동작 반영(당구공은 찌그러지지 않음, 농구공은 중력).</summary>
    private void UpdateSkinBehavior()
    {
        if (_animation != null)
            _animation.Rigid = _settings.Skin != SlimeSkinKind.Jelly; // 젤리만 말랑, 나머지는 단단

        bool basketball = _settings.Skin == SlimeSkinKind.Basketball;
        _physics.GravityY = basketball ? BasketballGravity : 0.0;
        // 중력으로 즉시 낙하하도록 렌더 루프를 깨운다(초기화 완료 후에만; _animation 준비 뒤).
        if (basketball && _animation != null) EnsureRendering();
    }

    // 창 이동은 매 프레임 일어나는 가장 비싼 작업이다. AllowsTransparency(레이어드) 창에서
    // WPF Left/Top 세터는 의존 속성 변경 → 레이아웃 → 재합성을 타서, 공이 빠를수록 눈에 띄게 렉이 걸린다.
    // 같은 이동을 Win32 SetWindowPos 로 직접 하면 그 경로를 통째로 건너뛴다.
    private const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                            int X, int Y, int cx, int cy, uint uFlags);

    private int _lastPosX = int.MinValue, _lastPosY = int.MinValue;

    private void ApplyWindowPosition()
    {
        // 물리 위치(슬라임 top-left)에서 패딩만큼 빼서 창 배치 → 슬라임은 화면상 Position 에 위치.
        double pad = EffectPadPx;
        double px = _physics.Position.X - pad;
        double py = _physics.Position.Y - pad;

        if (_hwnd != IntPtr.Zero)
        {
            // 물리 좌표가 이미 물리 픽셀이므로 DPI 변환 없이 그대로 쓴다(경계에서 더 정확).
            int x = (int)Math.Round(px), y = (int)Math.Round(py);
            if (x == _lastPosX && y == _lastPosY) return; // 같은 자리면 건너뜀
            _lastPosX = x; _lastPosY = y;
            SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            return;
        }

        // 핸들이 아직 없을 때(초기화 중) 폴백.
        Left = px / _dpiScaleX;
        Top = py / _dpiScaleY;
    }

    /// <summary>DPI·크기 변경 등으로 WPF 가 창을 다시 배치한 뒤, 다음 프레임에 강제로 재적용시킨다.</summary>
    private void InvalidateWindowPositionCache() => _lastPosX = _lastPosY = int.MinValue;

    /// <summary>스핀 이펙트 요소를 코드로 구성(불규칙 반짝이만; 좌우 블러 없음).</summary>
    private void BuildSpinFx()
    {
        var rnd = new Random(20240724);

        // 반짝이: 불규칙 위치/크기, 시간 기반 트윈클(렌더 루프에서 갱신 → 유휴 시 비용 없음)
        const int sparks = 10;
        for (int i = 0; i < sparks; i++)
        {
            double ang = rnd.NextDouble() * Math.PI * 2;
            double r = 56 + rnd.NextDouble() * 32;
            double size = 6 + rnd.NextDouble() * 9;
            var e = new Ellipse { Width = size, Height = size, Fill = SparkFill(), IsHitTestVisible = false };
            Canvas.SetLeft(e, 120 + r * Math.Cos(ang) - size / 2);
            Canvas.SetTop(e, 120 + r * Math.Sin(ang) - size / 2);
            SparkLayer.Children.Add(e);
            _sparkles.Add(e);
            _sparklePhase.Add(rnd.NextDouble() * Math.PI * 2);
        }
    }

    private static Brush SparkFill()
    {
        var b = new RadialGradientBrush();
        b.GradientStops.Add(new GradientStop(Color.FromRgb(255, 255, 255), 0.0));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0xEE, 255, 0xEC, 0x8A), 0.35));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0xCC, 255, 0xD2, 0x3A), 0.62));
        b.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 255, 0xD2, 0x3A), 1.0));
        b.Freeze();
        return b;
    }

    /// <summary>스핀 이펙트 갱신: 각속도로 세기, 스핀각으로 궤도 회전, 시간으로 반짝임.</summary>
    private void UpdateSpinFx()
    {
        double mag = Math.Abs(_physics.AngularVelocity);
        double denom = Math.Max(1.0, _settings.MaxAngularVelocity - _settings.SpinFxMinAngular);
        double intensity = Math.Clamp((mag - _settings.SpinFxMinAngular) / denom, 0, 1);
        SpinFx.Opacity = intensity;
        if (intensity <= 0) return;

        // 반짝이는 스핀 각도를 그대로 따라가지 않고, 완만한 고정 속도로 자체 회전 + 트윈클.
        double t = Now;
        SparkRotate.Angle = t * 22.0; // 초당 22도(스핀 세기·방향과 무관)
        for (int i = 0; i < _sparkles.Count; i++)
        {
            double tw = 0.3 + 0.7 * (0.5 + 0.5 * Math.Sin(t * (3.0 + i * 0.5) + _sparklePhase[i]));
            _sparkles[i].Opacity = tw;
        }
    }

    private void ResetPositionToCenter()
    {
        var wa = _monitors.PrimaryWorkingArea;
        double x = wa.Left + (wa.Width - _settings.SlimeSize) / 2.0;
        double y = wa.Top + (wa.Height - _settings.SlimeSize) / 2.0;
        _physics.Velocity = Vector2.Zero;
        _physics.Position = new Vector2(x, y);
        _animation?.ResetToRest();
    }

    // ── 입력 ────────────────────────────────────────────────
    private static Vector2 CursorPhysical()
    {
        var p = WinFormsCursor.Position; // 물리 픽셀(PerMonitorV2)
        return new Vector2(p.X, p.Y);
    }

    private bool IsCueMode => _settings.CueStickMode && _settings.Skin == SlimeSkinKind.Billiard;

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var c = CursorPhysical();
        if (IsCueMode)
        {
            if (InSpinCircle(c)) BeginSpinDrag(c); // 공 안쪽 클릭 → 스핀 점 이동
            else BeginAim(c);                       // 공 주위 클릭 → 큐대 조준
        }
        else BeginGrab(c);                          // 농구공 포함: 손으로 잡고 던지기(자유 드래그)
    }
    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var c = CursorPhysical();
        if (_spinDragging) SetSpinFromCursor(c);
        else if (_aiming) UpdateAim(c);
        else if (_isDragging) DragTo(c);
    }
    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var c = CursorPhysical();
        if (_spinDragging) { _spinDragging = false; try { ReleaseMouseCapture(); } catch { } }
        else if (_aiming) ReleaseAim(c);
        else if (_isDragging) ReleaseGrab(c);
    }

    // ── 스핀 조준 점(큐대 모드) ──────────────────────────────
    private bool _spinDragging;
    private Vector2 _spinOffset; // -1~1 정규화(공 중심 기준)

    private double SpinRadiusPx => _settings.SlimeSize * 0.33;

    private bool InSpinCircle(Vector2 cursor)
    {
        double half = _settings.SlimeSize / 2.0;
        Vector2 ballCenter = _physics.Position + new Vector2(half, half);
        return (cursor - ballCenter).Length <= SpinRadiusPx;
    }

    private void BeginSpinDrag(Vector2 cursor)
    {
        _spinDragging = true;
        try { CaptureMouse(); } catch { }
        SetSpinFromCursor(cursor);
    }

    private void SetSpinFromCursor(Vector2 cursor)
    {
        double half = _settings.SlimeSize / 2.0;
        Vector2 ballCenter = _physics.Position + new Vector2(half, half);
        Vector2 off = (cursor - ballCenter) / SpinRadiusPx;
        if (off.Length > 1) off = off.Normalized();
        _spinOffset = off;
        double dipR = SpinAim.Width / 2.0;
        if (double.IsNaN(dipR) || dipR <= 0) dipR = _settings.SlimeSize * 0.33 / _dpiScaleX;
        SpinDotShift.X = off.X * dipR;
        SpinDotShift.Y = off.Y * dipR;
    }

    private void UpdateSpinAimVisibility()
    {
        SpinAim.Visibility = IsCueMode ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 큐대 조준(당구공 전용) ──────────────────────────────
    private bool _aiming;
    private AimOverlayWindow? _aimOverlay;

    private void BeginAim(Vector2 cursor)
    {
        _aiming = true;
        _physics.Velocity = Vector2.Zero;
        _physics.AngularVelocity = 0;
        _physics.SurfaceSpin = 0;
        try { CaptureMouse(); } catch { }
        _aimOverlay ??= new AimOverlayWindow(_monitors);
        _aimOverlay.SetArcMode(false); // 큐대+직선 가이드(농구 조준과 오버레이 공유)
        _aimOverlay.Show();
        UpdateAim(cursor);
    }

    private (Vector2 dir, double power) AimParams(Vector2 cursor)
    {
        double half = _settings.SlimeSize / 2.0;
        Vector2 ballCenter = _physics.Position + new Vector2(half, half);
        Vector2 toBall = ballCenter - cursor;
        double dist = toBall.Length;
        Vector2 dir = dist > 1e-3 ? toBall / dist : new Vector2(1, 0);
        double pull = Math.Max(0, dist - _settings.SlimeSize * 0.44);
        double power = Math.Min(pull * _settings.CuePowerScale, _settings.MaxThrowSpeed);
        return (dir, power);
    }

    private void UpdateAim(Vector2 cursor)
    {
        double half = _settings.SlimeSize / 2.0;
        Vector2 ballCenter = _physics.Position + new Vector2(half, half);
        double radius = _settings.SlimeSize * 0.44;
        var (dir, power) = AimParams(cursor);
        double pull = Math.Max(0, (ballCenter - cursor).Length - radius);
        _aimOverlay?.UpdateAim(ballCenter, cursor, dir, Math.Clamp(power / _settings.MaxThrowSpeed, 0, 1), radius, pull);
    }

    private void ReleaseAim(Vector2 cursor)
    {
        _aiming = false;
        try { ReleaseMouseCapture(); } catch { }
        _aimOverlay?.Hide();
        var (dir, power) = AimParams(cursor);
        if (power > 60) // 최소 파워 이상일 때만 발사
        {
            _physics.Velocity = dir * power;
            // 세로 점 = 끌어치기/밀어치기(표면 스핀): 12시(위,-y)=밀어치기(전진), 6시(아래,+y)=끌어치기(되돌아옴)
            _physics.SpinShotDir = dir;
            _physics.SurfaceSpin = -_spinOffset.Y * power;
            // 가로 점 = 사이드 스핀(마그누스로 옆으로 휨): 3시(+x)/9시(-x)
            _physics.AngularVelocity = _spinOffset.X * _settings.MaxAngularVelocity;
            EnsureRendering();
        }
    }

    /// <summary>지정 커서(물리 px)에서 슬라임을 잡는다. WPF 클릭·전역 훅 공용.</summary>
    private void BeginGrab(Vector2 cursor)
    {
        _isDragging = true;
        _hasPrevBallY = false; // 잡는 동안 위치 점프 → 득점 오검출 방지
        _pressCursor = cursor;
        _dragOffset = cursor - _physics.Position;
        // 잡는 순간 속도/볼 열림 상태 기록(놓을 때 판정). 잡으면 즉시 정지.
        _grabbedSpeed = _physics.Velocity.Length;
        _ballWasOpen = SkinHost.Content is ISkinClickEffect b && b.IsOpen;
        _physics.Velocity = Vector2.Zero;
        // 잡으면 회전 충전 초기화(스핀도 잡아 멈춤)
        _physics.AngularVelocity = 0;
        _physics.SurfaceSpin = 0;
        _dragSpin = 0;
        _prevDragDir = Vector2.Zero;
        _lastDragCursor = cursor;
        _lastDragTime = Now;
        _tracker.Reset();
        _tracker.AddSample(cursor, Now);
        try { CaptureMouse(); } catch { /* 훅 경로에서는 무시 */ }
        EnsureRendering();
    }

    // ── 농구 조준(단축키를 누른 채 뒤로 끌기) ────────────────
    private bool _ballAiming;        // 조준 중(공 고정 + 포물선 유도선)
    private Vector2 _aimAnchor;      // 조준 기준점(공 중심)

    /// <summary>농구공이고 조준 단축키가 눌려 있는가.</summary>
    private bool AimKeyHeld =>
        _settings.Skin == SlimeSkinKind.Basketball
        && _settings.BasketballAimVk != 0
        && (GetAsyncKeyState(_settings.BasketballAimVk) & 0x8000) != 0;

    /// <summary>조준 파라미터: 당긴 거리로 발사 속도(포물선) 계산.</summary>
    private (Vector2 dir, double power) BallAimParams(Vector2 cursor)
    {
        Vector2 pull = _aimAnchor - cursor;        // 뒤로 끈 벡터 → 반대(=앵커 방향)로 날아간다
        double dist = pull.Length;
        Vector2 dir = dist > 1e-3 ? pull / dist : new Vector2(0, -1);
        double power = Math.Min(dist * BasketballAimScale, BasketballMaxThrow);
        return (dir, power);
    }

    /// <summary>유도선 표시 길이(약 10cm를 실제 화면 물리 px로 환산).</summary>
    private double AimShowLenPx => 10.0 * (96.0 * _dpiScaleX / 2.54);

    private void UpdateBallAim(Vector2 cursor)
    {
        var (dir, power) = BallAimParams(cursor);
        // 엔진과 동일한 중력/마찰을 넘겨 실제 궤적과 일치시킨다.
        _aimOverlay?.UpdateArc(_aimAnchor, dir * power, BasketballGravity, _physics.EffectiveFriction, AimShowLenPx);
    }

    private void BeginBallAim(Vector2 cursor)
    {
        _ballAiming = true;
        double half = _settings.SlimeSize / 2.0;
        _aimAnchor = _physics.Position + new Vector2(half, half); // 현재 공 위치를 발사 지점으로 고정
        _physics.Velocity = Vector2.Zero;
        _hasPrevBallY = false;
        _aimOverlay ??= new AimOverlayWindow(_monitors);
        _aimOverlay.SetArcMode(true);
        _aimOverlay.Show();
        UpdateBallAim(cursor);
    }

    private void EndBallAim(bool launch, Vector2 cursor)
    {
        _ballAiming = false;
        _aimOverlay?.Hide();
        if (!launch) return;
        var (dir, power) = BallAimParams(cursor);
        if (power > 60)
        {
            _physics.Velocity = dir * power;
            (SkinHost.Content as ISkinBounce)?.OnBounce();
        }
    }

    private void DragTo(Vector2 cursor)
    {
        double now = Now;

        // 조준 단축키를 누른 채 끌면: 공은 제자리에 고정되고 포물선 유도선이 나타난다.
        if (AimKeyHeld)
        {
            if (!_ballAiming) BeginBallAim(cursor);
            else UpdateBallAim(cursor);
            return;
        }
        if (_ballAiming) EndBallAim(launch: false, cursor); // 조준 중 키를 떼면 일반 드래그로 복귀

        // 드래그 곡선(curl)으로 스핀을 "충전"한다(관성). 한 방향으로 계속 돌리면
        // 스핀이 쌓여 유지되고, 직선 구간에서도 급히 사라지지 않는다.
        double dtm = now - _lastDragTime;
        Vector2 delta = cursor - _lastDragCursor;
        if (dtm > 1e-4 && delta.Length > 1.0)
        {
            Vector2 dir = delta.Normalized();
            if (_prevDragDir.LengthSquared > 1e-6)
            {
                double cross = _prevDragDir.X * dir.Y - _prevDragDir.Y * dir.X;
                double dot = _prevDragDir.X * dir.X + _prevDragDir.Y * dir.Y;
                double turnedDeg = Math.Atan2(cross, dot) * (180.0 / Math.PI); // 이번 샘플에서 꺾인 각(부호)
                _dragSpin += turnedDeg * _settings.SpinChargeGain;             // 누적(관성). 드래그 중엔 감쇠하지 않아 유지된다(던져야 소모).
                _dragSpin = Math.Clamp(_dragSpin, -_settings.MaxAngularVelocity, _settings.MaxAngularVelocity);
                _physics.AngularVelocity = _dragSpin;
            }
            _prevDragDir = dir;
            _lastDragCursor = cursor;
            _lastDragTime = now;
        }

        _physics.SetPositionClamped(cursor - _dragOffset);
        if (BowlingOn) ClampBallForPlacement(); // 볼링: 레인 위·파울선 위로만 놓을 수 있다
        _tracker.AddSample(cursor, now);
        ApplyWindowPosition();
    }

    private void ReleaseGrab(Vector2 cursor)
    {
        _isDragging = false;
        try { ReleaseMouseCapture(); } catch { }

        // 조준 중이었다면 유도선 방향/세기로 발사(마우스 속도 무시).
        if (_ballAiming)
        {
            EndBallAim(launch: true, cursor);
            EnsureRendering();
            return;
        }

        double moved = (cursor - _pressCursor).Length;

        switch (ClassifyRelease(moved, _grabbedSpeed))
        {
            case ReleaseAction.Throw:
                CloseBallIfOpen(); // 던지면 열린 볼은 닫힘
                if (_settings.ThrowMode)
                {
                    Vector2 throwV = _tracker.ComputeThrowVelocity(Now);
                    // 농구공은 던지기 가중치를 상쇄하고, 전용 감쇠 + 상한을 적용한다.
                    if (_settings.Skin == SlimeSkinKind.Basketball)
                    {
                        double throwPower = _settings.ThrowPower;
                        if (throwPower > 0.01 && Math.Abs(throwPower - 1.0) > 0.01)
                            throwV /= throwPower;
                        throwV = (throwV * BasketballThrowScale).ClampLength(BasketballMaxThrow);
                    }
                    _physics.Velocity = throwV;
                }
                // 볼링: 기름칠 레인 보정(최소 속도 + 항상 전진)
                if (BowlingOn && !_gameOver) ApplyBowlingLaunch();
                break;

            case ReleaseAction.CatchHold:
                CloseBallIfOpen();
                if (_settings.Skin == SlimeSkinKind.Jelly)
                    _animation.Punch(); // 젤리만 잡히는 느낌의 작은 스쿼시
                break;

            case ReleaseAction.Deflect:
                CloseBallIfOpen();
                if (_settings.PunchMode) Deflect(cursor, _grabbedSpeed);
                else _animation.Punch(); // 때리기를 껐으면 반응만
                break;

            case ReleaseAction.Click:
                if (_settings.PunchMode)
                    DoClickEffect(cursor);
                break;
        }

        EnsureRendering();
    }

    private void CloseBallIfOpen()
    {
        if (SkinHost.Content is ISkinClickEffect b && b.IsOpen) b.SetOpen(false);
    }

    private enum ReleaseAction { Throw, CatchHold, Click, Deflect }

    /// <summary>
    /// 놓을 때 동작 판정.
    ///  - 많이 움직였으면 던지기
    ///  - 날아가던 걸 제자리 클릭했으면 되치기(속도 유지 + 방향 전환)
    ///  - 느리게 움직이던 걸 잡았으면 낚아채기
    ///  - 그 외 정지 상태 클릭
    /// </summary>
    private ReleaseAction ClassifyRelease(double movedPx, double grabbedSpeed)
    {
        if (movedPx >= _settings.ClickMoveThreshold) return ReleaseAction.Throw;
        if (grabbedSpeed >= _settings.DeflectMinSpeed) return ReleaseAction.Deflect;
        if (grabbedSpeed > _settings.CatchSpeedThreshold) return ReleaseAction.CatchHold;
        return ReleaseAction.Click;
    }

    /// <summary>
    /// 되치기: 날아가던 공을 클릭한 지점 반대쪽으로 튕겨 보낸다.
    /// 속도를 죽이지 않고(원래 속도 유지, 최소 PunchImpulse) 방향만 바꿔 라켓으로 치는 느낌.
    /// </summary>
    private void Deflect(Vector2 cursor, double incomingSpeed)
    {
        Vector2 center = _physics.Position + new Vector2(_settings.SlimeSize / 2.0, _settings.SlimeSize / 2.0);
        Vector2 dir = (center - cursor).Normalized();
        if (dir.LengthSquared < 1e-6) dir = new Vector2(0, -1); // 정확히 중앙을 눌렀으면 위로

        double speed = Math.Max(incomingSpeed, _settings.PunchImpulse);
        _physics.Velocity = dir * Math.Min(speed, _settings.MaxSpeed);

        // 맞은 세기는 들어온 속도 기준 — 빠른 공을 되치면 더 크게 반응한다.
        double intensity = Math.Clamp(incomingSpeed / _settings.ImpactReferenceSpeed, 0.25, 1.0);
        _animation.OnImpact(incomingSpeed);
        _particles.Emit(cursor, intensity, ImpactTier.Boing);
        _hitText.Spawn(center, intensity);
        _audio.PlayPunch(0.5 + 0.5 * intensity);
    }

    // ── 렌더 루프 ───────────────────────────────────────────
    private void EnsureRendering()
    {
        if (BowlingOn) StartBowlingLoop(); // 볼링 중이면 핀 물리 루프도 함께 깨운다
        if (_renderingActive) return;
        _renderingActive = true;
        _lastFrameTime = Now;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopRendering()
    {
        if (!_renderingActive) return;
        _renderingActive = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        double now = Now;
        double dt = now - _lastFrameTime;
        _lastFrameTime = now;
        if (dt <= 0) return;
        if (dt > _settings.MaxFrameDeltaSeconds)
            dt = _settings.MaxFrameDeltaSeconds; // 큰 점프(스톨/포커스 복귀) 방지

        // 파티클은 어떤 상태에서도 항상 진행·렌더(드래그/일시정지 중에도 잔여 효과 소멸).
        bool particlesAlive = _particles.Update(dt);
        _overlay?.Render(_particles.Active);
        bool hitTextAlive = _hitText.Update(dt);
        _hitTextOverlay?.Render(_hitText.Active);
        bool netsAlive = TickHoops(dt); // 골대 그물 스프링(어느 상태에서도 진행)

        if (_isDragging)
        {
            SetExpression(SlimeExpression.Normal);
            _physics.SpinAngle += _physics.AngularVelocity * dt; // 충전 중 시각 회전
            ApplyWindowPosition();
            _animation.Tick(dt, Vector2.Zero, _physics.SpinAngle);
            UpdateSpinFx();
            return;
        }

        if (_settings.Paused)
        {
            _animation.Tick(dt, Vector2.Zero, _physics.SpinAngle);
            if (_physics.IsAtRest && _animation.IsResting && !particlesAlive && !hitTextAlive && !netsAlive)
                StopRendering();
            return;
        }

        PhysicsStepResult r = _physics.Update(dt);
        if (r.Collided)
        {
            _animation.OnImpact(r.MaxImpactSpeed);
            TriggerImpactEffects(r.MaxImpactSpeed, r.CollisionNormal, r.CollisionPosition);
            if (r.MaxImpactSpeed > _settings.ImpactReferenceSpeed * DizzyImpactFraction)
                _dizzyUntil = now + DizzyDurationSeconds;
            // 농구공: 벽에 튈 때마다 씸 무늬 변경
            (SkinHost.Content as ISkinBounce)?.OnBounce();
        }

        ResolveHoopCollisions();
        CheckScoring();
        if (BowlingOn) UpdateBallLane(_settings.SlimeSize * 0.42); // 볼링: 파울선·거터 규칙
        // 네트워크: 공이 연결된 엣지를 넘었으면 다른 PC로 핸드오프(넘기고 공을 숨김).
        if (_networked && _connected && _ownsBall && _coord!.CheckAndSendHandoff(now))
        {
            HideBallForHandoff();
            return;
        }

        ApplyWindowPosition();
        _animation.Tick(dt, _physics.Velocity, _physics.SpinAngle);
        UpdateSkinRoll(dt);
        UpdateSpinFx();

        // 표정: 어질(충돌 직후) > 신남(빠름) > 평상
        SetExpression(
            now < _dizzyUntil ? SlimeExpression.Dizzy
            : _physics.Velocity.Length > _settings.ImpactReferenceSpeed * FlyingSpeedFraction ? SlimeExpression.Flying
            : SlimeExpression.Normal);

        // 완전히 멈추고 형태도 안정되고 파티클도 없고 표정도 원상복귀되면 루프 정지(유휴).
        if (r.Sleeping && _animation.IsResting && !particlesAlive && !hitTextAlive && !netsAlive && now >= _dizzyUntil)
            StopRendering();
    }

    /// <summary>충돌 세기를 단계로 분류해 스킨에 맞는 이펙트를 발동한다.</summary>
    /// <param name="hitPos">충돌 순간의 슬라임 top-left(물리 픽셀). 프레임 종료 위치를 쓰면
    /// 빠를 때 한 프레임 이동량(최대 MaxSpeed×MaxFrameDelta)만큼 빗나가 화면 안쪽에 이펙트가 뜬다.</param>
    private void TriggerImpactEffects(double impactSpeed, Vector2 normal, Vector2 hitPos)
    {
        ImpactTier tier = ImpactClassifier.Classify(impactSpeed, _settings);
        if (tier == ImpactTier.None) return;

        double intensity = ImpactClassifier.Intensity01(impactSpeed, _settings);
        Vector2 center = hitPos + new Vector2(_settings.SlimeSize / 2.0, _settings.SlimeSize / 2.0);

        if (_settings.Skin != SlimeSkinKind.Jelly)
        {
            // 단단한 스킨(당구공/몬스터볼): 슬라임 스플랫 대신 "쿠션에 탁!"
            // 벽 접점에서 접선 방향 스파크 + 딱딱한 소리
            Vector2 contact = center - normal * (_settings.SlimeSize / 2.0);
            _particles.EmitCushion(contact, normal, intensity, ImpactTier.Bonk);
            _audio.Play(ImpactTier.Bonk, intensity);
        }
        else
        {
            _particles.Emit(center, intensity, tier);
            _audio.Play(tier, intensity);
            _hitText.Spawn(center, intensity); // 젤리: 벽에 부딪히면 "Hit!" 문구
        }
    }

    /// <summary>정지 상태 클릭 반응(스킨별). 젤리=펀치, 당구공=딱 튕김, 몬스터볼=열림 이펙트.</summary>
    private void DoClickEffect(Vector2 cursor)
    {
        Vector2 center = _physics.Position + new Vector2(_settings.SlimeSize / 2.0, _settings.SlimeSize / 2.0);

        if (SkinHost.Content is ISkinClickEffect ball)
        {
            // 볼 계열: 클릭 시 열림/닫힘 토글(잡을 때 상태 기준). 빛/파티클 없이 조용히.
            ball.SetOpen(!_ballWasOpen);
            _audio.Play(ImpactTier.Bonk, 0.45);
            return;
        }

        Vector2 dir = (center - cursor).Normalized();
        if (dir.LengthSquared < 1e-6) dir = new Vector2(0, -1);
        _physics.Velocity = dir * _settings.PunchImpulse;

        if (_settings.Skin == SlimeSkinKind.Billiard)
        {
            // 당구공: 찌그러짐 없이 딱 튕김
            _audio.PlayPunch(0.6);
            return;
        }

        // 젤리: 찌그러지며 튕김 + 작은 파티클 + 타격 문구
        _animation.Punch();
        _particles.Emit(cursor, 0.4, ImpactTier.Boing);
        _hitText.Spawn(center, 0.55); // 때리면 "Pow!" 등
        _audio.PlayPunch(0.6);
    }

    // ── 모니터 구성 변경 ────────────────────────────────────
    private void OnMonitorLayoutChanged(object? sender, EventArgs e)
    {
        // MonitorLayoutService.Refresh() 는 이미 수행된 상태(IWalkableArea 갱신됨).
        UpdateDpiScale();
        ApplyWindowSize();

        // 슬라임이 사라진 모니터 위에 있었다면 주 모니터 중앙으로 되돌린다.
        if (!_physics.IsCurrentPositionValid())
            ResetPositionToCenter();

        ApplyWindowPosition();
        RespawnHoops(); // 모니터 구성이 바뀌면 골대도 새 벽 위치로 다시 세운다
        EnsureRendering();
    }

    // ── 설정 변경 반영 ──────────────────────────────────────
    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.AlwaysOnTop):
                Topmost = _settings.AlwaysOnTop;
                break;
            case nameof(AppSettings.ShowInTaskbar):
                ShowInTaskbar = _settings.ShowInTaskbar;
                break;
            case nameof(AppSettings.ClickMarginPx):
                ApplyWindowSize(); // 클릭 원 크기 갱신
                break;
            case nameof(AppSettings.Paused):
                if (!_settings.Paused) EnsureRendering();
                break;
            case nameof(AppSettings.SlimeVisible):
                ApplyVisibility();
                break;
            case nameof(AppSettings.Skin):
                ClearExtraBalls(); // 테마(스킨) 바꾸면 놓았던 3/4구 당구공도 함께 치운다
                ClearHoops();      // 농구 골대도 치운다
                ExitBowling();     // 볼링 모드도 정리
                ApplySkin();
                break;
            case nameof(AppSettings.CueStickMode):
                UpdateSpinAimVisibility();
                break;
            case nameof(AppSettings.SkinImages):
            case nameof(AppSettings.SkinImageEnabled):
            case nameof(AppSettings.SkinImageScale):
                ApplyCustomImage();
                break;
            case nameof(AppSettings.CatchHotkeyMod):
            case nameof(AppSettings.CatchHotkeyVk):
            case nameof(AppSettings.CatchHotkeyMouse):
            case nameof(AppSettings.HideHotkeyMod):
            case nameof(AppSettings.HideHotkeyVk):
            case nameof(AppSettings.HideHotkeyMouse):
                RegisterHotkeys();
                break;
            case nameof(AppSettings.SlimeSize):
                ApplyWindowSize();
                if (!_physics.IsCurrentPositionValid())
                    _physics.SetPositionClamped(_physics.Position);
                ApplyWindowPosition();
                ResizeHoops();
                break;
        }
    }

    /// <summary>선택된 스킨(UserControl)을 스킨 호스트에 넣는다. 스킨 추가는 여기만 확장.</summary>
    private void ApplySkin()
    {
        SkinHost.Content = _settings.Skin switch
        {
            // 당구공: 4구/3구 중이면 흰 수구, 아니면 검은 8번공
            SlimeSkinKind.Billiard => _extraBalls.Count > 0
                ? new BilliardSkin(BilliardSkin.Cue)
                : new BilliardSkin(),
            SlimeSkinKind.Pokeball or SlimeSkinKind.Ultra or SlimeSkinKind.Master
                => new BallSkin(_settings.Skin),
            SlimeSkinKind.Basketball => new BasketballSkin(),
            SlimeSkinKind.Bowling => new BowlingSkin(),
            _ => new JellySkin(),
        };
        _expression = SlimeExpression.Normal; // 새 스킨은 기본 표정으로 시작
        UpdateSkinBehavior();
        UpdateSpinAimVisibility();
        ApplyCustomImage();
    }

    /// <summary>현재 테마의 커스텀 이미지를 공 위에 덧씌운다(없거나 끄면 숨김).</summary>
    private void ApplyCustomImage()
    {
        var img = _settings.SkinImageEnabled && SkinImageStore.Supports(_settings.Skin)
            ? SkinImageStore.Load(_settings.Skin)
            : null;

        CustomImage.Source = img;
        CustomImageLayer.Visibility = img != null ? Visibility.Visible : Visibility.Collapsed;
        if (img == null) return;

        // 디자인 캔버스(96) 기준 공 지름은 84. 배율 1.0 = 공에 꽉 맞춤, 초과분은 원으로 클립된다.
        double d = 84.0 * Math.Clamp(_settings.SkinImageScale, 0.2, 2.0);
        CustomImage.Width = d;
        CustomImage.Height = d;
    }

    /// <summary>굴러가며 무늬가 바뀌는 스킨(볼링공)에 이번 프레임 회전수를 전달.</summary>
    private void UpdateSkinRoll(double dt)
    {
        if (SkinHost.Content is not ISkinRolling rolling) return;
        double size = _settings.SlimeSize;
        if (size <= 0) return;
        double dist = _physics.Velocity.Length * dt;
        if (dist <= 0) return;
        double revs = dist / (Math.PI * size);          // 이동거리 → 공 지름 기준 회전수
        if (_physics.Velocity.X < 0) revs = -revs;        // 왼쪽으로 굴러가면 반대로 넘김
        rolling.OnRoll(revs);
    }

    /// <summary>표정 변경(스킨이 표정을 지원할 때만). 상태가 바뀔 때만 반영.</summary>
    private void SetExpression(SlimeExpression e)
    {
        if (e == _expression) return;
        _expression = e;
        if (SkinHost.Content is ISkinExpressions skin)
            skin.SetExpression(e);
    }

    // ── 외부(설정창)에서 호출하는 public API ─────────────────
    public void ResetPositionPublic()
    {
        ResetPositionToCenter();
        ApplyWindowPosition();
    }

    // ── 컨텍스트 메뉴 ───────────────────────────────────────
    private SettingsWindow? _settingsWindow;

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow == null)
        {
            _settingsWindow = new SettingsWindow(_settings, this);
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;
                ApplyWindowSize(); // 설정창이 닫히면 클릭 여유를 원래대로
            };
        }
        _settingsWindow.Show();
        _settingsWindow.Activate();
        ApplyWindowSize();         // 설정창이 열린 동안에는 공 크기로만 클릭을 받는다
    }

    /// <summary>
    /// 설정창이 열려 있는가. 열려 있는 동안에는 공 주변 클릭 여유를 없앤다.
    ///
    /// 여유가 넓으면 날아가는 공을 되치기 쉽지만, 그만큼 보이지 않는 원이 공을 따라다니며
    /// 뒤에 있는 창의 클릭을 가린다. 설정을 만지는 동안에는 조작이 우선이므로 공 그림만큼만 받는다.
    /// </summary>
    private bool SettingsOpen => _settingsWindow is { IsVisible: true };

    private void OnResetPosition(object sender, RoutedEventArgs e) => ResetPositionPublic();

    /// <summary>트레이 등 외부에서 설정 창을 여는 진입점.</summary>
    public void OpenSettingsPublic() => OnOpenSettings(this, new RoutedEventArgs());

    private void OnExit(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    // ── 당구 4구/3구 (추가 공 생성) ─────────────────────────
    private readonly List<ExtraBallWindow> _extraBalls = new();
    private readonly Random _ballRng = new();

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        bool bil = _settings.Skin == SlimeSkinKind.Billiard;
        var vis = bil ? Visibility.Visible : Visibility.Collapsed;
        MenuBilliardSep.Visibility = vis;
        Menu4Ball.Visibility = vis;
        Menu3Ball.Visibility = vis;
        MenuClearBalls.Visibility = _extraBalls.Count > 0 && bil ? Visibility.Visible : Visibility.Collapsed;

        // 농구공 전용: 골대 생성/치우기
        bool bball = _settings.Skin == SlimeSkinKind.Basketball;
        var bvis = bball ? Visibility.Visible : Visibility.Collapsed;
        MenuBasketballSep.Visibility = bvis;
        MenuHoops.Visibility = bvis;
        MenuHoops.Header = _hoops.Count > 0 ? "농구골대 치우기" : "농구골대 구현";

        // 볼링공 테마일 때만 볼링 메뉴 노출
        bool bowl = _settings.Skin == SlimeSkinKind.Bowling;
        MenuBowlingSep.Visibility = bowl ? Visibility.Visible : Visibility.Collapsed;
        MenuBowlingStart.Visibility = bowl && !BowlingOn ? Visibility.Visible : Visibility.Collapsed;
        MenuBowlingReset.Visibility = bowl && BowlingOn ? Visibility.Visible : Visibility.Collapsed;
        MenuBowlingExit.Visibility = bowl && BowlingOn ? Visibility.Visible : Visibility.Collapsed;

        // 현재 버전(안내용). 설정 → 업데이트 노트에서 무엇이 바뀌었는지 볼 수 있다.
        MenuVersion.Header = $"ThrowMe v{UpdateService.Current.ToString(3)}";
    }

    private void On4Ball(object sender, RoutedEventArgs e) => SpawnBalls(2, 1);
    private void On3Ball(object sender, RoutedEventArgs e) => SpawnBalls(1, 1);
    private void OnClearBalls(object sender, RoutedEventArgs e) => ClearExtraBalls();

    private void SpawnBalls(int reds, int yellows)
    {
        ClearExtraBalls();
        for (int i = 0; i < reds; i++) SpawnBall(Skins.BilliardSkin.Red);
        for (int i = 0; i < yellows; i++) SpawnBall(Skins.BilliardSkin.Yellow);
        if (_settings.Skin == SlimeSkinKind.Billiard) ApplySkin(); // 수구(흰색)로 전환
        StartBilliardLoop();
    }

    private void SpawnBall(Color color)
    {
        var wa = _monitors.PrimaryWorkingArea;
        double size = _settings.SlimeSize;
        double x = wa.Left + _ballRng.NextDouble() * Math.Max(1, wa.Width - size);
        double y = wa.Top + _ballRng.NextDouble() * Math.Max(1, wa.Height - size);
        double ang = _ballRng.NextDouble() * Math.PI * 2;
        double spd = 1000 + _ballRng.NextDouble() * 1600;
        var vel = new Vector2(Math.Cos(ang) * spd, Math.Sin(ang) * spd);

        var ball = new ExtraBallWindow(color, _settings, _monitors, new Vector2(x, y), vel);
        _extraBalls.Add(ball);
        ball.Show();
    }

    private void ClearExtraBalls()
    {
        StopBilliardLoop();
        foreach (var b in _extraBalls) { try { b.Close(); } catch { } }
        _extraBalls.Clear();
        if (_settings.Skin == SlimeSkinKind.Billiard) ApplySkin(); // 8번공으로 복귀
    }

    // ── 당구 물리 루프(공-공 충돌 포함) ─────────────────────
    private bool _billiardActive;
    private double _billiardLastTime;

    private void StartBilliardLoop()
    {
        if (_billiardActive) return;
        _billiardActive = true;
        _billiardLastTime = Now;
        CompositionTarget.Rendering += OnBilliardTick;
    }

    private void StopBilliardLoop()
    {
        if (!_billiardActive) return;
        _billiardActive = false;
        CompositionTarget.Rendering -= OnBilliardTick;
    }

    private void OnBilliardTick(object? sender, EventArgs e)
    {
        double now = Now;
        double dt = now - _billiardLastTime;
        _billiardLastTime = now;
        if (dt <= 0) return;
        if (dt > _settings.MaxFrameDeltaSeconds) dt = _settings.MaxFrameDeltaSeconds;

        // 추가 공 물리 적분(수구는 SlimeWindow 자체 루프가 적분)
        foreach (var b in _extraBalls) b.Physics.Update(dt);

        ResolveBallCollisions();

        foreach (var b in _extraBalls) b.ApplyPosition();
    }

    /// <summary>수구+추가 공들 간 원-원 탄성 충돌(동일 질량, 캐롬 느낌).</summary>
    private void ResolveBallCollisions()
    {
        double windowHalf = _settings.SlimeSize / 2.0;      // 창 중심(=공 중심)
        double r = _settings.SlimeSize * 0.44;              // 실제 보이는 공 반경(스킨 여백 반영)
        double minDist = r * 2.0;                            // 표면이 맞닿는 중심거리

        // 인덱스 0 = 수구(SlimeWindow._physics), 1.. = 추가 공
        var engines = new List<SlimePhysicsEngine> { _physics };
        foreach (var b in _extraBalls) engines.Add(b.Physics);

        var half = new Vector2(windowHalf, windowHalf);
        bool cueChanged = false;

        for (int i = 0; i < engines.Count; i++)
        for (int j = i + 1; j < engines.Count; j++)
        {
            var a = engines[i]; var b2 = engines[j];
            Vector2 ca = a.Position + half, cb = b2.Position + half;
            Vector2 d = cb - ca;
            double dist = d.Length;
            if (dist <= 1e-4 || dist >= minDist) continue;

            Vector2 n = d / dist;
            double overlap = minDist - dist;
            // 겹침 분리(각각 절반씩) 후, 벽/작업표시줄 밖으로 나가지 않게 클램프
            a.Position -= n * (overlap * 0.5);
            b2.Position += n * (overlap * 0.5);
            a.SetPositionClamped(a.Position);
            b2.SetPositionClamped(b2.Position);

            Vector2 rv = b2.Velocity - a.Velocity;
            double vn = rv.X * n.X + rv.Y * n.Y;
            if (vn < 0) // 접근 중일 때만 반발
            {
                double e = 0.94; // 당구공 반발
                double jimp = -(1 + e) * vn / 2.0; // 동일 질량
                a.Velocity -= n * jimp;
                b2.Velocity += n * jimp;
                if (i == 0) cueChanged = true;
            }
        }

        if (cueChanged) EnsureRendering(); // 수구가 맞았으면 수구 루프 깨우기
    }

    // ── 농구 골대 (좌/우 모니터) ────────────────────────────
    // 골대 ON 시 물리를 "현실 농구공"에 가깝게 고정(사용자 설정 대신 이 값 사용 — 설정 슬라이더는 무시).
    private const double HoopRestitution = 0.62; // 반발(살살: 높이의 ~38% 리바운드)
    private const double HoopFriction = 0.35;    // 공기저항(낮게 → 자연스런 포물선)

    // 뒷판/스윗스팟(뱅크샷): 유리판은 에너지를 크게 죽여 공이 멀리 튕겨나가지 않는다.
    private const double BackboardRestitution = 0.30; // 림(0.62)보다 훨씬 낮게 → "죽는" 반발
    private const double BankAssist = 0.40;           // 스윗스팟 유도 비율(살짝 낮춤)
    private const double BankUpKill = 0.45;           // 위로 튀는 성분 억제(공이 내려앉게)
    // 빠르게 던져 맞힌 공은 유도를 덜 받는다(멀리서 세게 던져도 잘 들어가는 문제 완화).
    private const double BankSpeedSoft = 1400.0;      // 이 속도까지는 유도 100%
    private const double BankSpeedHard = 4200.0;      // 이 속도면 유도 최소치

    private readonly List<HoopWindow> _hoops = new();
    private ScoreboardWindow? _boardLeft, _boardRight;   // 각 모니터 최상단 점수판
    private int _scoreLeft, _scoreRight;                 // 좌/우 모니터 점수판의 값
    private Vector2 _prevBallCenter;
    private bool _hasPrevBallY;

    private void OnToggleHoops(object sender, RoutedEventArgs e)
    {
        if (_hoops.Count > 0) ClearHoops();
        else SpawnHoops();
    }

    /// <summary>가장 왼쪽 모니터 좌벽 + 가장 오른쪽 모니터 우벽에 골대를 세우고, 각 모니터 최상단에 점수판을 둔다.</summary>
    private void SpawnHoops()
    {
        ClearHoops();
        var areas = _monitors.WorkingAreas;
        if (areas.Count == 0) return;

        Rect leftArea = areas[0], rightArea = areas[0];
        foreach (var a in areas)
        {
            if (a.Left < leftArea.Left) leftArea = a;
            if (a.Right > rightArea.Right) rightArea = a;
        }

        AddHoop(HoopSide.Left, leftArea);
        AddHoop(HoopSide.Right, rightArea);

        // 새 경기: 점수 초기화 + 각 모니터 최상단 점수판 생성.
        // 단일 모니터면 두 점수판이 겹치지 않게 좌/우로 벌린다.
        _scoreLeft = _scoreRight = 0;
        bool sameMonitor = leftArea == rightArea;
        _boardLeft = new ScoreboardWindow(leftArea, sameMonitor ? 0.25 : 0.5, _settings); _boardLeft.Show();
        _boardRight = new ScoreboardWindow(rightArea, sameMonitor ? 0.75 : 0.5, _settings); _boardRight.Show();

        // 골대 ON: 물리를 현실 농구공에 가깝게 고정
        _physics.RestitutionOverride = HoopRestitution;
        _physics.FrictionOverride = HoopFriction;

        _hasPrevBallY = false;
        EnsureRendering();
    }

    private void AddHoop(HoopSide side, Rect area)
    {
        var h = new HoopWindow(side, area, _settings);
        _hoops.Add(h);
        h.Show();
    }

    private void ClearHoops()
    {
        foreach (var h in _hoops) { try { h.Close(); } catch { } }
        _hoops.Clear();
        try { _boardLeft?.Close(); } catch { }
        try { _boardRight?.Close(); } catch { }
        _boardLeft = _boardRight = null;

        // 골대 OFF: 물리 오버라이드 해제(사용자 설정으로 복귀)
        _physics.RestitutionOverride = null;
        _physics.FrictionOverride = null;
    }

    private void RespawnHoops()
    {
        if (_hoops.Count > 0) SpawnHoops();
    }

    /// <summary>경기 상태는 유지한 채 현재 공 크기 비율로 골대만 다시 만든다.</summary>
    private void ResizeHoops()
    {
        if (_hoops.Count == 0) return;

        foreach (var h in _hoops) { try { h.Close(); } catch { } }
        _hoops.Clear();

        var areas = _monitors.WorkingAreas;
        if (areas.Count == 0) return;

        Rect leftArea = areas[0], rightArea = areas[0];
        foreach (var area in areas)
        {
            if (area.Left < leftArea.Left) leftArea = area;
            if (area.Right > rightArea.Right) rightArea = area;
        }

        AddHoop(HoopSide.Left, leftArea);
        AddHoop(HoopSide.Right, rightArea);
        _hasPrevBallY = false;
        EnsureRendering();
    }

    /// <summary>그물 스프링 적분/렌더. 아직 흔들리는 골대가 있으면 true.</summary>
    private bool TickHoops(double dt)
    {
        bool alive = false;
        for (int i = 0; i < _hoops.Count; i++)
            alive |= _hoops[i].UpdateNet(dt);
        return alive;
    }

    /// <summary>림(빨간 가장자리)·백보드에 공이 부딪히면 튕겨낸다. 충돌 시 그물도 흔들고 무늬를 바꾼다.</summary>
    private void ResolveHoopCollisions()
    {
        if (_hoops.Count == 0) return;
        double rBall = _settings.SlimeSize * 0.44;
        double half = _settings.SlimeSize / 2.0;
        Vector2 c = _physics.Position + new Vector2(half, half);
        Vector2 v = _physics.Velocity;
        bool anyHit = false;

        foreach (var h in _hoops)
        {
            bool hit = false;
            foreach (var edge in h.RimEdges)
                hit |= ResolveCircle(ref c, ref v, edge, h.RimEdgeRadius, rBall, HoopRestitution);

            // 뒷판: 반발이 낮아(에너지 죽음) 멀리 튕기지 않는다.
            double speedBefore = v.Length;
            double hitY = c.Y;
            if (ResolveRect(ref c, ref v, h.Backboard, rBall, BackboardRestitution))
            {
                hit = true;
                // 스윗스팟(파란 구역)을 맞힌 경우에만 림으로 유도 → 나머지 뒷판은 그냥 죽은 반발.
                bool inSweet = hitY >= h.SweetSpot.Top - rBall && hitY <= h.SweetSpot.Bottom + rBall;
                if (inSweet && hitY < h.RimCenter.Y)
                {
                    // 세게 맞힐수록 유도가 약해진다(멀리서 강슛이 그냥 들어가지 않게).
                    double t = Math.Clamp((speedBefore - BankSpeedSoft) / (BankSpeedHard - BankSpeedSoft), 0, 1);
                    double assist = BankAssist * (1.0 - 0.75 * t);
                    double toRimX = h.RimCenter.X - c.X;
                    v = new Vector2(v.X * (1 - assist) + toRimX * assist * 2.2, v.Y);
                    if (v.Y < 0) v = v.WithY(v.Y * BankUpKill); // 위로 튀는 성분 억제
                }
            }

            if (hit)
            {
                anyHit = true;
                h.Nudge(v);
                (SkinHost.Content as ISkinBounce)?.OnBounce();
                _audio.Play(ImpactTier.Bonk, 0.4);
            }
        }

        if (anyHit)
        {
            _physics.SetPositionClamped(c - new Vector2(half, half));
            _physics.Velocity = v;
        }
    }

    private static bool ResolveCircle(ref Vector2 c, ref Vector2 v, Vector2 p, double rEdge, double rBall, double restitution)
    {
        Vector2 d = c - p;
        double dist = d.Length, min = rEdge + rBall;
        if (dist >= min || dist < 1e-6) return false;
        Vector2 n = d / dist;
        c += n * (min - dist); // 겹침 밀어내기
        double vn = v.X * n.X + v.Y * n.Y;
        if (vn < 0) { v -= n * ((1 + restitution) * vn); return true; }
        return false;
    }

    private static bool ResolveRect(ref Vector2 c, ref Vector2 v, Rect rect, double rBall, double restitution)
    {
        double cx = Math.Clamp(c.X, rect.Left, rect.Right);
        double cy = Math.Clamp(c.Y, rect.Top, rect.Bottom);
        Vector2 d = c - new Vector2(cx, cy);
        double dist = d.Length;
        if (dist >= rBall) return false;

        Vector2 n;
        if (dist < 1e-6) // 중심이 사각형 안쪽 → 가장 가까운 면으로
        {
            double dl = c.X - rect.Left, dr = rect.Right - c.X, dt = c.Y - rect.Top, db = rect.Bottom - c.Y;
            double mn = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
            n = mn == dl ? new Vector2(-1, 0) : mn == dr ? new Vector2(1, 0)
              : mn == dt ? new Vector2(0, -1) : new Vector2(0, 1);
            c += n * (rBall + mn);
        }
        else { n = d / dist; c += n * (rBall - dist); }

        double vn = v.X * n.X + v.Y * n.Y;
        if (vn < 0) { v -= n * ((1 + restitution) * vn); return true; }
        return false;
    }

    /// <summary>
    /// 공이 림 개구부를 위→아래로 통과하면 득점. 교차 채점:
    /// 왼쪽 골대가 먹히면 오른쪽 점수판 +, 오른쪽 골대가 먹히면 왼쪽 점수판 +.
    /// </summary>
    private void CheckScoring()
    {
        if (_hoops.Count == 0) return;
        double half = _settings.SlimeSize / 2.0;
        Vector2 center = _physics.Position + new Vector2(half, half);
        Vector2 vel = _physics.Velocity;
        double now = Now;

        if (_hasPrevBallY)
        {
            double dy = center.Y - _prevBallCenter.Y;
            foreach (var h in _hoops)
            {
                // 위→아래 통과. dy > 0 자체가 하강이므로 속도(vel.Y)는 보지 않는다.
                // 림을 스치며 들어간 공은 통과 프레임에서 vel.Y 가 위로 뒤집혀 골이 취소되곤 했다.
                bool crossedDown = dy > 1e-9
                                && _prevBallCenter.Y <= h.RimCenter.Y && center.Y >= h.RimCenter.Y;
                if (!crossedDown || now < h.ScoreCooldownUntil) continue;

                // 림 높이를 지나는 "순간"의 X 로 판정한다. 프레임 끝 X 로 재면
                // 비스듬히 들어온 공이 이미 개구부를 지나쳐 있어 골이 누락됐다.
                double t = (h.RimCenter.Y - _prevBallCenter.Y) / dy;
                double crossX = _prevBallCenter.X + (center.X - _prevBallCenter.X) * t;
                if (Math.Abs(crossX - h.RimCenter.X) <= h.RimHalfWidth)
                {
                    h.OnScored(vel);
                    h.ScoreCooldownUntil = now + 0.6;
                    _audio.Play(ImpactTier.Bonk, 0.5); // 스위시 대용 효과음
                    (SkinHost.Content as ISkinBounce)?.OnBounce();

                    // 교차 채점: 먹힌 골대의 반대쪽 점수판 +1
                    if (h.Side == HoopSide.Left) { _scoreRight++; _boardRight?.SetScore(_scoreRight); }
                    else { _scoreLeft++; _boardLeft?.SetScore(_scoreLeft); }
                }
            }
        }

        _prevBallCenter = center;
        _hasPrevBallY = true;
    }

    // ── 볼링 모드 (레인 + 10핀) ─────────────────────────────
    private readonly List<PinWindow> _pins = new();
    private LaneOverlayWindow? _lane;
    private BowlingScoreboardWindow? _bowlingScoreboard;
    private bool _bowlingActive;        // 볼링 물리 루프 가동 중
    private double _bowlingLastTime;
    private BowlingLayout _bl;          // 현재 레인 지오메트리(가둠 판정에 사용)
    private int _ballGutterSide;        // 0=레인 위, -1=왼쪽 거터, +1=오른쪽 거터
    private Vector2 _ballPrevPos;       // 직전 프레임 공 위치(터널링 방지 서브스텝용)
    private bool _ballPrevInit;
    private double? _savedSlimeSize;    // 볼링 진입 전 사용자 크기(종료 시 복원)
    private double? _savedFriction;     // 볼링 진입 전 마찰(기름칠 레인 복원용)
    private const double BowlingSize = 96.0;   // 볼링은 항상 기본 크기로 진행
    // 기름칠 레인: 기본(0.9)보다 훨씬 덜 감속해 매끄럽게 굴러간다.
    // 단, 던진 세기는 그대로 반영되므로 살살 굴리면 끝까지 못 간다.
    private const double OiledFriction = 0.35;
    private bool _bowlingOn;
    private bool BowlingOn => _bowlingOn;

    // ── 게임 진행(10프레임, 마지막 프레임은 보너스 포함 최대 3구) ────
    private const int TotalFrames = 10;
    private readonly List<int>[] _frameRolls =
        Enumerable.Range(0, TotalFrames).Select(_ => new List<int>(3)).ToArray();
    private int _frame = 1;             // 1..10
    private int _throwNo = 1;           // 1 또는 2, 10프레임 보너스 투구는 3
    private int _totalScore;
    private bool _gameOver;
    private bool _ballLaunched;         // 이번 투구가 던져졌는가
    private bool _ballReachedEnd;       // 공이 레인 끝(핀 뒤 벽)에 닿았는가
    private bool _ballHiddenForReset;   // 핀덱 뒤로 빠진 공을 다음 투구까지 숨겼는가
    private double _finishThrowAt;      // >0 이면 이 시각에 투구 마무리 처리
    private string? _banner;            // STRIKE!/거터! 등 임시 배너
    private double _bannerUntil;

    private void OnBowlingStart(object sender, RoutedEventArgs e)
    {
        if (_settings.Skin != SlimeSkinKind.Bowling) return;
        SetupBowling();
    }

    /// <summary>핀 다시 세우기 = 게임을 1프레임부터 새로 시작.</summary>
    private void OnBowlingReset(object sender, RoutedEventArgs e)
    {
        if (!BowlingOn) return;
        _bl = ComputeBowlingLayout();
        StartNewGame();
    }

    private void StartNewGame()
    {
        _frame = 1;
        _throwNo = 1;
        _totalScore = 0;
        _gameOver = false;
        _banner = null;
        foreach (var rolls in _frameRolls) rolls.Clear();
        ResetBallToStart();    // 핀덱의 공을 먼저 회수해 새 핀과 겹치지 않게 한다
        RespawnPins(_bl);
        UpdateHud();
    }

    private void OnBowlingExit(object sender, RoutedEventArgs e) => ExitBowling();

    private void SetupBowling()
    {
        ExitBowling(); // 중복 방지

        // 볼링은 항상 기본 크기(default)로. 사용자 크기를 기억했다가 종료 시 복원.
        _savedSlimeSize = _settings.SlimeSize;
        if (Math.Abs(_settings.SlimeSize - BowlingSize) > 0.5)
            _settings.SlimeSize = BowlingSize; // PropertyChanged → 창/물리 즉시 재적용

        // 기름칠 레인: 마찰을 낮춰 매끄럽게 굴러가게 한다(세기 보정은 하지 않는다).
        _savedFriction = _settings.Friction;
        _settings.Friction = OiledFriction;

        // 던지기 가중치는 볼링 동안 1.0x 로 취급한다. 단 설정값 자체는 바꾸지 않는다
        // (자동저장이 사용자 값을 덮어쓰지 않도록) — 발사 시점에 나눠서 상쇄한다.

        _bowlingOn = true;
        _bl = ComputeBowlingLayout();

        _lane = new LaneOverlayWindow(_monitors);
        _lane.Show(); // Show() 시점에 창 위치/배율 확정
        _lane.Setup(_bl.CenterX, _bl.TopY, _bl.BotY, _bl.FoulY,
                    _bl.LaneHalfTop, _bl.LaneHalfBot, _bl.AlleyHalfTop, _bl.AlleyHalfBot,
                    _bl.DeckBotY, _bl.ArrowsY);

        _bowlingScoreboard = new BowlingScoreboardWindow(_monitors.PrimaryWorkingArea);
        _bowlingScoreboard.Show();

        StartNewGame();        // 핀 세우기 + 공 시작점 + HUD
        RaiseMainWindowTop();  // 공 창을 최상단으로(레인·핀 위)
        StartBowlingLoop();
    }

    private void RespawnPins(BowlingLayout layout)
    {
        ClearPins();
        foreach (var c in layout.PinCenters)
        {
            var pin = new PinWindow(_settings, _monitors, c);
            _pins.Add(pin);
            pin.Show();
        }
    }

    private void ClearPins()
    {
        foreach (var p in _pins) { try { p.Close(); } catch { } }
        _pins.Clear();
    }

    /// <summary>볼링 모드 종료: 루프 정지 + 핀·레인 정리 + 공 Topmost·크기·마찰 복원.</summary>
    private void ExitBowling()
    {
        _bowlingOn = false;
        StopBowlingLoop();
        ClearPins();
        if (_lane != null) { try { _lane.Close(); } catch { } _lane = null; }
        if (_bowlingScoreboard != null)
        {
            try { _bowlingScoreboard.Close(); } catch { }
            _bowlingScoreboard = null;
        }
        Topmost = _settings.AlwaysOnTop;
        if (_savedSlimeSize.HasValue)
        {
            _settings.SlimeSize = _savedSlimeSize.Value; // 사용자 크기 복원
            _savedSlimeSize = null;
        }
        if (_savedFriction.HasValue)
        {
            _settings.Friction = _savedFriction.Value;   // 마찰 복원
            _savedFriction = null;
        }
    }

    private void MoveBallTo(Vector2 center)
    {
        double half = _settings.SlimeSize / 2.0;
        _physics.Velocity = Vector2.Zero;
        _physics.AngularVelocity = 0;
        _physics.SurfaceSpin = 0;
        _ballGutterSide = 0;          // 새 투구: 거터 상태 해제
        _physics.SetPositionClamped(center - new Vector2(half, half));
        _ballPrevPos = _physics.Position;
        _ballPrevInit = true;
        ApplyWindowPosition();
        EnsureRendering();
    }

    // ── 멀티 PC 릴레이 처리 ─────────────────────────────────
    /// <summary>설정창 표시용: 현재 릴레이 설정/상태/링크.</summary>
    public AuthService RelayAuth => _auth;
    public RelayState RelayState => _relay?.State ?? RelayState.Disabled;
    public IReadOnlyList<EdgeLinkDto> CurrentLinks => _auth.Links;

    // ── 방(파티) 상태 ────────────────────────────────────────
    /// <summary>방장 nodeId(서버 권위). 배치는 방장만 바꿀 수 있다.</summary>
    public string? RoomHost { get; private set; }
    /// <summary>파티 순서(좌 → 우).</summary>
    public IReadOnlyList<string> RoomOrder => _roomOrder;
    /// <summary>현재 방에 접속해 있는 노드들.</summary>
    public IReadOnlyList<NodePresenceDto> RoomNodes => _roomNodes;
    /// <summary>이 PC가 방장인가.</summary>
    public bool IsHost => !string.IsNullOrEmpty(RoomHost) && RoomHost == _selfNodeId;
    /// <summary>내 노드 이름(서버가 확정한 값).</summary>
    public string SelfNodeId => _selfNodeId;

    private List<string> _roomOrder = new();
    private List<NodePresenceDto> _roomNodes = new();

    /// <summary>방 구성(멤버·순서·방장)이 바뀌면 발생 — 설정창이 목록을 갱신한다.</summary>
    public event Action? RoomStateChanged;

    /// <summary>방장이 파티 순서를 바꿀 때 호출. 서버에 순서를 알리고 배치(링크)를 배포한다.</summary>
    public void PushPartyOrder(IReadOnlyList<string> order)
    {
        if (_relay == null || !IsHost) return;

        _ = _relay.SendAsync(new Envelope
        {
            Type = MsgType.SetOrder,
            From = _selfNodeId,
            Data = RelayJson.ToElement(new SetOrderData { Order = order.ToList() }),
        });

        // 순서 → 좌우 체인 배치로 변환해 방 전체에 배포.
        PushRoomConfig(PartyLayout.BuildChainLinks(order));
    }

    /// <summary>방장 위임(방장만). 대상은 접속 중이어야 한다.</summary>
    public void TransferHost(string toNodeId)
    {
        if (_relay == null || !IsHost || string.IsNullOrWhiteSpace(toNodeId)) return;
        if (toNodeId == _selfNodeId) return;

        _ = _relay.SendAsync(new Envelope
        {
            Type = MsgType.TransferHost,
            From = _selfNodeId,
            Data = RelayJson.ToElement(new TransferHostData { To = toNodeId }),
        });
    }

    /// <summary>방 공통 테마 지정(방장만). 방 전원의 테마가 함께 바뀐다.</summary>
    public void PushRoomTheme(SlimeSkinKind skin)
    {
        if (_relay == null || !IsHost) return;
        _ = _relay.SendAsync(new Envelope
        {
            Type = MsgType.SetTheme,
            From = _selfNodeId,
            Data = RelayJson.ToElement(new SetThemeData { Theme = skin.ToString() }),
        });
    }

    /// <summary>서버가 알려준 방 공통 테마를 이 PC에 적용.</summary>
    private void ApplyRoomTheme(string? theme)
    {
        if (string.IsNullOrWhiteSpace(theme)) return;
        if (!Enum.TryParse<SlimeSkinKind>(theme, ignoreCase: true, out var skin)) return;
        if (_settings.Skin == skin) return;
        _settings.Skin = skin; // PropertyChanged → ApplySkin
    }

    /// <summary>서버가 알려준 방 상태를 반영. 방장이면 순서에 맞는 배치를 자동으로 맞춘다.</summary>
    private void ApplyRoomState(string? host, List<string> order, List<NodePresenceDto> nodes, string? theme = null)
    {
        RoomHost = host;
        if (order.Count > 0) _roomOrder = order;
        _roomNodes = nodes;
        ApplyRoomTheme(theme);

        // 방장은 "순서 = 배치"를 보장한다.
        // 배치는 반드시 **지금 접속해 있는 노드만**으로 만든다. 서버의 order 는 나간 사람도
        // 계속 남기 때문에, 그대로 쓰면 접속하지도 않은 PC 로 향하는 링크가 생기고
        // 그 방향으로 던진 공이 갈 곳이 없어진다(공이 사라진 것처럼 보였던 원인 중 하나).
        if (IsHost)
        {
            var online = new HashSet<string>(nodes.Select(n => n.NodeId), StringComparer.Ordinal);
            var chain = _roomOrder.Where(online.Contains).ToList();
            foreach (var id in online) if (!chain.Contains(id)) chain.Add(id);

            var want = PartyLayout.BuildChainLinks(chain);
            if (!PartyLayout.SameLinks(want, _auth.Links))
                PushRoomConfig(want);
        }

        Logger.Info($"Room state: host={host} owner-sync order=[{string.Join(",", _roomOrder)}] " +
                    $"online=[{string.Join(",", nodes.Select(n => n.NodeId))}]");

        RoomStateChanged?.Invoke();
    }

    /// <summary>릴레이 활성화(설정 존재 시). 물리 area 를 네트워크 인지형으로 교체.</summary>
    private void SetupNetworking()
    {
        _networked = true;
        _selfNodeId = _auth.NodeId;
        _netArea = new NetworkedWalkableArea(_monitors);
        _physics.Area = _netArea; // 연결된 엣지로는 나가도 유효 → 반사 대신 핸드오프
        _relay = new RelayClient(_auth);
        _coord = new BallHandoffCoordinator(
            _physics, _settings, _netArea,
            () => { var vb = _monitors.VirtualBounds; return new Bounds(vb.Left, vb.Top, vb.Width, vb.Height); },
            env => { if (_relay != null) _ = _relay.SendAsync(env); })
        { SelfNodeId = _selfNodeId };
        if (_auth.Links.Count > 0) _coord.SetLinks(_auth.Links);
        _relay.MessageReceived += env => Dispatcher.InvokeAsync(() => OnRelayMessage(env));
        _relay.StateChanged += st => Dispatcher.InvokeAsync(() => OnRelayState(st));
    }

    /// <summary>릴레이 비활성화. 로컬 판정으로 복귀하고 공을 다시 로컬 표시.</summary>
    private void TeardownNetworking()
    {
        var relay = _relay;
        _relay = null;
        if (relay != null) { _ = relay.StopAsync(); relay.Dispose(); }
        _coord = null;
        _netArea = null;
        _networked = false;
        _connected = false;
        _physics.Area = _monitors;

        // 방을 나갔으니 파티 정보(멤버·순서·방장)를 비운다. 안 그러면 설정창 목록에
        // 나간 사람들이 그대로 남는다.
        RoomHost = null;
        _roomOrder = new List<string>();
        _roomNodes = new List<NodePresenceDto>();
        RoomStateChanged?.Invoke();

        // 공을 되찾는다. 이때 위치 검증이 중요하다 — 공을 남에게 넘긴 직후라면 위치가
        // 경계 밖(넘어가던 좌표)이라, 그냥 표시하면 화면 밖에 있어 안 보이고 물리도 정지해
        // 앱을 껐다 켜야 했다.
        if (!_ownsBall)
        {
            _ownsBall = true;
            if (!_physics.IsCurrentPositionValid()) ResetPositionToCenter();
            if (_settings.SlimeVisible) Show();
            ApplyWindowPosition();
            EnsureRendering();
        }
        else if (!_physics.IsCurrentPositionValid())
        {
            // 소유 중이었더라도 경계 밖이면(연결 엣지로 나가던 중 끊김) 되돌린다.
            ResetPositionToCenter();
            ApplyWindowPosition();
            EnsureRendering();
        }
    }

    /// <summary>설정창이 <see cref="RelayAuth"/> 값을 바꾼 뒤 호출. 저장 후 네트워킹 재초기화.</summary>
    public void ApplyRelaySettings()
    {
        _auth.Save();
        TeardownNetworking();
        RelayStateChanged?.Invoke(Network.RelayState.Disabled);
        if (_auth.IsConfigured)
        {
            SetupNetworking();
            _relay?.Start();
        }
    }

    /// <summary>
    /// 방 참여 시 링크 병합. <b>자기 노드에서 나가는 링크는 이 PC가 권위</b>를 갖고,
    /// 다른 노드의 링크는 서버 것을 그대로 보존한다. 서버와 달라지면 병합 결과를 방에 배포.
    /// (이렇게 해야 나중에 접속한 PC 의 매핑이 먼저 접속한 PC 의 매핑에 덮이지 않는다.)
    /// </summary>
    private void MergeLinksOnJoin(List<EdgeLinkDto> serverLinks)
    {
        // 로컬에 캐시된 링크에는 **다른 방/예전 세션의 상대**가 남아 있을 수 있다.
        // 그대로 병합해 올리면 이 방에 있지도 않은 PC 로 향하는 링크가 생기고,
        // 그쪽으로 던진 공은 갈 곳이 없어진다. 방에 실제로 있는 노드만 남긴다.
        var known = new HashSet<string>(_roomNodes.Select(n => n.NodeId), StringComparer.Ordinal);
        known.Add(_selfNodeId);
        foreach (var l in serverLinks) { known.Add(l.From); known.Add(l.To); }

        var localUsable = _auth.Links.Where(l => known.Contains(l.From) && known.Contains(l.To)).ToList();

        var merged = LinkMerge.Merge(_selfNodeId, localUsable, serverLinks);
        _auth.Links = merged;
        _coord?.SetLinks(merged);

        if (!LinkMerge.Same(merged, serverLinks) && _relay != null)
        {
            _ = _relay.SendAsync(new Envelope
            {
                Type = MsgType.RoomConfig, From = _selfNodeId,
                Data = RelayJson.ToElement(new RoomConfigData { Links = merged }),
            });
        }
    }

    /// <summary>엣지 매핑을 저장하고 서버(방 전체)에 배포.</summary>
    public void PushRoomConfig(IReadOnlyList<EdgeLinkDto> links)
    {
        _auth.Links = links.ToList();
        _auth.Save();
        _coord?.SetLinks(_auth.Links);
        if (_relay != null)
        {
            _ = _relay.SendAsync(new Envelope
            {
                Type = MsgType.RoomConfig,
                From = _selfNodeId,
                Data = RelayJson.ToElement(new RoomConfigData { Links = _auth.Links }),
            });
        }
    }

    private void OnRelayState(RelayState st)
    {
        RelayStateChanged?.Invoke(st);
        _connected = st == RelayState.Connected;
        // 서버가 끊기면(장애/오프라인) 이 PC가 공을 로컬로 표시해 토이를 계속 쓸 수 있게 한다.
        // 재연결 시 WELCOME/PRESENCE 로 소유권이 다시 동기화된다.
        // 넘기던 중이었으면 위치가 경계 밖이라 화면 밖에 놓이므로 중앙으로 되돌린다.
        if (!_connected && !_ownsBall)
            GainBall(spawnAtCenter: !_physics.IsCurrentPositionValid());
    }

    private void OnRelayMessage(Envelope env)
    {
        switch (env.Type)
        {
            case MsgType.Welcome:
                if (env.DataAs<WelcomeData>() is { } w)
                {
                    _selfNodeId = string.IsNullOrEmpty(w.NodeId) ? _selfNodeId : w.NodeId;
                    if (_coord != null) _coord.SelfNodeId = _selfNodeId;
                    // 방장이면 순서 기반 배치가 권위 → 링크 병합 대신 순서에서 재생성한다.
                    if (!string.IsNullOrEmpty(w.Host) && w.Host == _selfNodeId && w.Order.Count > 1)
                    {
                        _auth.Links = w.Links;
                        _coord?.SetLinks(w.Links);
                    }
                    else
                    {
                        MergeLinksOnJoin(w.Links);
                    }
                    ApplyRoomState(w.Host, w.Order, w.Nodes, w.Theme);
                    ApplyOwnership(w.Owner);
                }
                break;

            case MsgType.Presence:
                if (env.DataAs<PresenceData>() is { } p)
                {
                    ApplyRoomState(p.Host, p.Order, p.Nodes, p.Theme);
                    ApplyOwnership(p.Owner);
                }
                break;

            case MsgType.SetTheme:
                // 방장이 방 테마를 바꿈 → 전원 즉시 반영.
                if (env.DataAs<SetThemeData>() is { } th) ApplyRoomTheme(th.Theme);
                break;

            case MsgType.RoomConfig:
                if (env.DataAs<RoomConfigData>() is { } cfg) { _auth.Links = cfg.Links; _coord?.SetLinks(cfg.Links); }
                break;

            case MsgType.Handoff:
                // 다른 PC가 공을 넘겨옴 → 물리 주입 + ACK. 성공 시 표시.
                if (_coord?.ApplyIncoming(env) == true) GainBall(spawnAtCenter: false);
                break;

            case MsgType.HandoffResult:
                if (env.DataAs<HandoffResultData>() is { } res && _coord != null)
                {
                    _handoffWatchdog?.Stop(); // 결과가 왔으니 안전망 해제
                    switch (_coord.OnResult(res))
                    {
                        case BallHandoffCoordinator.ResultKind.Released:
                            LoseBall();                       // 상대가 받음 → 공 해제
                            break;
                        case BallHandoffCoordinator.ResultKind.Reflected:
                            _ownsBall = true;                 // 실패 → 내가 계속 소유
                            ShowBall();
                            EnsureRendering();                // 반사로 회수, 계속 표시
                            break;
                    }
                }
                break;

            case MsgType.Error:
                if (env.DataAs<ErrorData>() is { } err)
                {
                    Logger.Info($"Relay error: {err.Code} {err.Message}");

                    // 넘기는 중에 ERROR 가 오면 HANDOFF_RESULT 는 영영 오지 않는다.
                    // (예: 서버가 소유자로 인정하지 않아 NOT_OWNER) 여기서 회수하지 않으면
                    // 공이 사라진 채로 남아 앱을 다시 켜야 했다.
                    if (_coord != null && _coord.HasPendingOut
                        && _coord.OnRejected(err.Code) == BallHandoffCoordinator.ResultKind.Reflected)
                    {
                        _handoffWatchdog?.Stop();
                        _ownsBall = true;
                        ShowBall();
                        ApplyWindowPosition();
                        EnsureRendering();
                    }
                }
                break;
        }
    }

    /// <summary>서버가 알려준 소유자에 맞춰 이 PC의 공 표시/숨김을 동기화.</summary>
    private void ApplyOwnership(string? owner)
    {
        bool mine = owner != null && owner == _selfNodeId;
        if (mine && !_ownsBall)
        {
            GainBall(spawnAtCenter: true);   // (재)소유 획득 — 이양 등
            NotifyBallMoved(null);
        }
        else if (!mine && _ownsBall)
        {
            LoseBall();
            NotifyBallMoved(owner);
        }
        _ownsBall = mine;
    }

    /// <summary>
    /// 공 위치가 바뀌었음을 우측 하단 토스트로 알린다.
    /// 공이 다른 PC로 넘어가면 이 PC 화면에서 슬라임이 사라지는데, 알림이 없으면
    /// 앱이 죽은 것처럼 보인다(실제로 그렇게 오해하기 쉬웠다).
    /// </summary>
    /// <param name="newOwner">공을 가져간 PC 이름. null 이면 이 PC가 받았다는 뜻.</param>
    private void NotifyBallMoved(string? newOwner)
    {
        if (!_settings.ShowToasts) return;

        try
        {
            if (newOwner == null)
                ToastWindow.Show("공이 도착했습니다", "이제 이 PC 에서 던질 수 있어요.");
            else
                ToastWindow.Show($"공이 {newOwner} 로 넘어갔습니다",
                    $"{newOwner} 에서 이쪽으로 던지면 다시 돌아옵니다.");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to notify ball handoff.", ex);
        }
    }

    private void GainBall(bool spawnAtCenter)
    {
        // 종료 중에 릴레이 메시지가 늦게 도착하면 이미 닫힌 창에 Show() 를 호출해
        // InvalidOperationException("창이 닫힘 후에는 Show 할 수 없습니다")이 반복됐다.
        if (_shuttingDown) return;

        _ownsBall = true;
        if (spawnAtCenter) ResetPositionToCenter();
        if (_settings.SlimeVisible) Show();
        ApplyWindowPosition();
        EnsureRendering();
    }

    /// <summary>공 창을 최상단으로 끌어올린다(나중에 뜬 레인/핀 위로).</summary>
    private void RaiseMainWindowTop()
    {
        Topmost = false;
        Topmost = true;
    }

    // ── 투구 진행 ───────────────────────────────────────────
    /// <summary>공을 시작점(파울선)에 다시 놓고 다음 투구를 준비.</summary>
    private void ResetBallToStart()
    {
        bool restoreBall = _ballHiddenForReset;
        _ballLaunched = false;
        _ballReachedEnd = false;
        _ballHiddenForReset = false;
        _finishThrowAt = 0;
        MoveBallTo(_bl.BallStart);
        if (restoreBall) ShowBall();
    }

    /// <summary>
    /// 볼링 투구 시작 표시. 던진 세기는 사용자의 손놀림 그대로 두고(보정 없음),
    /// 터널링 방지를 위한 상한만 건다. 레인이 기름칠돼 있어 굴러가는 느낌만 매끄럽다.
    /// </summary>
    private void ApplyBowlingLaunch()
    {
        // 사용자의 "던지기 가중치"를 상쇄해 볼링에서는 항상 1.0x(실제 손놀림 그대로)로 던진다.
        double tp = _settings.ThrowPower;
        if (tp > 0.01 && Math.Abs(tp - 1.0) > 0.01) _physics.Velocity /= tp;

        double maxSpeed = _settings.SlimeSize * 45.0;
        if (_physics.Velocity.Length > maxSpeed)
            _physics.Velocity = _physics.Velocity.ClampLength(maxSpeed);

        // 파울선 뒤에서 공을 들어 옮겼다 놓는 동작은 투구가 아니다.
        // 핀 쪽(화면 위)으로 충분히 던졌을 때만 이번 투구를 시작한다.
        const double minForwardSpeed = 120.0;
        if (_physics.Velocity.Y >= -minForwardSpeed)
        {
            _physics.Velocity = Vector2.Zero;
            return;
        }

        _ballLaunched = true;
        _ballReachedEnd = false;
        _finishThrowAt = 0;
    }

    private int CountKnockedPins()
    {
        int n = 0;
        foreach (var p in _pins) if (p.Knocked) n++;
        return n;
    }

    /// <summary>쓰러진 핀을 치운다(남은 핀은 그 자리에 그대로).</summary>
    private void RemoveKnockedPins()
    {
        for (int i = _pins.Count - 1; i >= 0; i--)
        {
            if (!_pins[i].Knocked) continue;
            try { _pins[i].Close(); } catch { }
            _pins.RemoveAt(i);
        }
        RestoreStandingPins();
    }

    /// <summary>이번 투구에서 쓰러지지 않은 핀은 원래 핀 번호의 자리로 되돌린다.</summary>
    private void RestoreStandingPins()
    {
        double half = _settings.SlimeSize / 2.0;
        foreach (var pin in _pins)
        {
            pin.Physics.Position = pin.StandCenter - new Vector2(half, half);
            pin.Physics.Velocity = Vector2.Zero;
            pin.Physics.AngularVelocity = 0;
            pin.Physics.SurfaceSpin = 0;
            pin.ApplyPosition();
        }
    }

    /// <summary>공이 끝까지 굴러가 멈춘 뒤 호출 — 투구 기록, 표준 점수 계산, 다음 투구 준비.</summary>
    private void FinishThrow()
    {
        int knocked = Math.Clamp(CountKnockedPins(), 0, 10);
        _frameRolls[_frame - 1].Add(knocked);

        if (_frame < TotalFrames)
            FinishRegularFrame(knocked);
        else
            FinishTenthFrame(knocked);

        RecalculateTotalScore();
        UpdateHud();
    }

    private void FinishRegularFrame(int knocked)
    {
        if (_throwNo == 1)
        {
            if (knocked >= 10)
            {
                ShowBanner("STRIKE! 🎳", Color.FromRgb(0xFF, 0xD1, 0x3A));
                AdvanceFrame();
            }
            else
            {
                if (knocked == 0 && _ballGutterSide != 0)
                    ShowBanner("거터! 😵", Color.FromRgb(0xFF, 0x9B, 0x6A));
                RemoveKnockedPins();
                _throwNo = 2;
                ResetBallToStart();
            }
            return;
        }

        int framePins = _frameRolls[_frame - 1].Sum();
        if (framePins >= 10)
            ShowBanner("SPARE! ✨", Color.FromRgb(0x9B, 0xE8, 0x7A));
        else if (knocked == 0 && _ballGutterSide != 0)
            ShowBanner("거터! 😵", Color.FromRgb(0xFF, 0x9B, 0x6A));
        AdvanceFrame();
    }

    /// <summary>10프레임은 스트라이크/스페어 시 최대 3구까지 진행한다.</summary>
    private void FinishTenthFrame(int knocked)
    {
        List<int> rolls = _frameRolls[TotalFrames - 1];
        if (_throwNo == 1)
        {
            if (knocked >= 10)
            {
                ShowBanner("STRIKE! 🎳", Color.FromRgb(0xFF, 0xD1, 0x3A));
                PrepareFullRackForNextThrow(2);
            }
            else
            {
                if (knocked == 0 && _ballGutterSide != 0)
                    ShowBanner("거터! 😵", Color.FromRgb(0xFF, 0x9B, 0x6A));
                RemoveKnockedPins();
                _throwNo = 2;
                ResetBallToStart();
            }
            return;
        }

        if (_throwNo == 2)
        {
            int first = rolls[0];
            if (first >= 10)
            {
                if (knocked >= 10)
                {
                    ShowBanner("STRIKE! 🎳", Color.FromRgb(0xFF, 0xD1, 0x3A));
                    PrepareFullRackForNextThrow(3);
                }
                else
                {
                    RemoveKnockedPins();
                    _throwNo = 3;
                    ResetBallToStart();
                }
            }
            else if (first + knocked >= 10)
            {
                ShowBanner("SPARE! ✨", Color.FromRgb(0x9B, 0xE8, 0x7A));
                PrepareFullRackForNextThrow(3);
            }
            else
            {
                if (knocked == 0 && _ballGutterSide != 0)
                    ShowBanner("거터! 😵", Color.FromRgb(0xFF, 0x9B, 0x6A));
                EndBowlingGame();
            }
            return;
        }

        if (knocked >= 10)
            ShowBanner("STRIKE! 🎳", Color.FromRgb(0xFF, 0xD1, 0x3A));
        else if (rolls.Count >= 3 && rolls[0] >= 10 && rolls[1] < 10 && rolls[1] + rolls[2] >= 10)
            ShowBanner("SPARE! ✨", Color.FromRgb(0x9B, 0xE8, 0x7A));
        EndBowlingGame();
    }

    private void AdvanceFrame()
    {
        _frame++;
        _throwNo = 1;
        RespawnPins(_bl);      // 다음 프레임의 10핀을 먼저 세운 뒤
        ResetBallToStart();    // 숨긴 공을 투구 위치에 다시 배치한다
    }

    private void PrepareFullRackForNextThrow(int nextThrow)
    {
        _throwNo = nextThrow;
        RespawnPins(_bl);      // 새 랙을 먼저 세운 뒤
        ResetBallToStart();    // 보너스 공을 투구 위치에 다시 배치한다
    }

    private void EndBowlingGame()
    {
        _frame = TotalFrames;
        _gameOver = true;
        ClearPins();
        ResetBallToStart();
    }

    private void RecalculateTotalScore()
    {
        _totalScore = 0;
        foreach (int? score in CalculateCumulativeScores())
            if (score.HasValue) _totalScore = score.Value;
    }

    private int?[] CalculateCumulativeScores()
    {
        var result = new int?[TotalFrames];
        int running = 0;

        for (int i = 0; i < TotalFrames; i++)
        {
            List<int> rolls = _frameRolls[i];
            if (rolls.Count == 0) break;

            int frameScore;
            if (i < TotalFrames - 1)
            {
                if (rolls[0] >= 10)
                {
                    List<int> bonus = FollowingRolls(i, 2);
                    if (bonus.Count < 2) break;
                    frameScore = 10 + bonus[0] + bonus[1];
                }
                else
                {
                    if (rolls.Count < 2) break;
                    int basePins = rolls[0] + rolls[1];
                    if (basePins >= 10)
                    {
                        List<int> bonus = FollowingRolls(i, 1);
                        if (bonus.Count < 1) break;
                        frameScore = 10 + bonus[0];
                    }
                    else
                    {
                        frameScore = basePins;
                    }
                }
            }
            else
            {
                if (!IsFrameComplete(i)) break;
                frameScore = rolls.Sum();
            }

            running += frameScore;
            result[i] = running;
        }
        return result;
    }

    private List<int> FollowingRolls(int frameIndex, int count)
    {
        var result = new List<int>(count);
        for (int i = frameIndex + 1; i < TotalFrames && result.Count < count; i++)
        {
            foreach (int pins in _frameRolls[i])
            {
                result.Add(pins);
                if (result.Count == count) break;
            }
        }
        return result;
    }

    private bool IsFrameComplete(int frameIndex)
    {
        List<int> rolls = _frameRolls[frameIndex];
        if (rolls.Count == 0) return false;
        if (frameIndex < TotalFrames - 1)
            return rolls[0] >= 10 || rolls.Count >= 2;
        if (rolls.Count < 2) return false;
        bool bonus = rolls[0] >= 10 || rolls[0] + rolls[1] >= 10;
        return bonus ? rolls.Count >= 3 : rolls.Count >= 2;
    }

    private IReadOnlyList<BowlingFrameDisplay> BuildScoreboardFrames()
    {
        int?[] cumulative = CalculateCumulativeScores();
        var display = new List<BowlingFrameDisplay>(TotalFrames);
        for (int i = 0; i < TotalFrames; i++)
        {
            List<int> rolls = _frameRolls[i];
            string first = "", second = "", third = "";

            if (i < TotalFrames - 1)
            {
                if (rolls.Count > 0)
                {
                    if (rolls[0] >= 10) second = "X";
                    else first = PinMark(rolls[0]);
                }
                if (rolls.Count > 1)
                    second = rolls[0] + rolls[1] >= 10 ? "/" : PinMark(rolls[1]);
            }
            else
            {
                if (rolls.Count > 0) first = PinMark(rolls[0]);
                if (rolls.Count > 1)
                {
                    second = rolls[0] < 10 && rolls[0] + rolls[1] >= 10
                        ? "/"
                        : PinMark(rolls[1]);
                }
                if (rolls.Count > 2)
                {
                    third = rolls[0] >= 10 && rolls[1] < 10 && rolls[1] + rolls[2] >= 10
                        ? "/"
                        : PinMark(rolls[2]);
                }
            }

            display.Add(new BowlingFrameDisplay(
                first, second, third, cumulative[i], IsFrameComplete(i)));
        }
        return display;
    }

    private static string PinMark(int pins)
        => pins >= 10 ? "X" : pins <= 0 ? "–" : pins.ToString();
    private void ShowBanner(string text, Color color)
    {
        _banner = text;
        _bannerColor = color;
        _bannerUntil = Now + 1.8;
    }

    private Color _bannerColor = Colors.White;

    private void UpdateHud()
    {
        if (_bowlingScoreboard == null) return;

        string status;
        Color color;
        if (_gameOver)
        {
            status = $"GAME COMPLETE · {_totalScore}점";
            color = Color.FromRgb(0xFF, 0xD1, 0x3A);
        }
        else if (_banner != null && Now < _bannerUntil)
        {
            status = _banner;
            color = _bannerColor;
        }
        else
        {
            _banner = null;
            status = "READY · 공을 굴려주세요";
            color = Color.FromRgb(0xA9, 0xD9, 0xF7);
        }

        _bowlingScoreboard.SetGame(
            _frame,
            _throwNo,
            _totalScore,
            BuildScoreboardFrames(),
            status,
            color,
            _gameOver);
    }
    private readonly struct BowlingLayout
    {
        public double CenterX { get; init; }
        public double TopY { get; init; }      // 핀 뒤 벽(원경, 좁음)
        public double BotY { get; init; }      // 레인 시각 하단(근경, 넓음)
        public double FoulY { get; init; }     // 파울 라인(공이 넘지 못하는 하한)
        public double LaneHalfTop { get; init; }
        public double LaneHalfBot { get; init; }
        public double AlleyHalfTop { get; init; }
        public double AlleyHalfBot { get; init; }
        public double DeckBotY { get; init; }
        public double ArrowsY { get; init; }
        public double BallExitY { get; init; } // 뒤쪽 핀과 재충돌하기 전에 공을 회수할 지점
        public Vector2 BallStart { get; init; }
        public List<Vector2> PinCenters { get; init; }
    }

    /// <summary>주 모니터 작업영역 기준 원근 레인·핀·공 위치 계산(모두 물리 px).</summary>
    private BowlingLayout ComputeBowlingLayout()
    {
        System.Windows.Rect wa = _monitors.PrimaryWorkingArea;
        double S = _settings.SlimeSize;

        double topY = wa.Top + wa.Height * 0.045;          // 원경(핀 뒤)
        double botY = wa.Bottom - wa.Height * 0.04;        // 근경(투구석 하단)
        double foulY = wa.Bottom - wa.Height * 0.15;       // 파울 라인(공 하한)

        double cx = wa.Left + wa.Width / 2.0;
        // 레인 반폭: 위(좁음) → 아래(넓음)로 원근. 화면을 넘지 않게 상한.
        double laneHalfBot = Math.Min(wa.Width * 0.30, S * 2.2);
        double laneHalfTop = laneHalfBot * 0.64;
        // 거터 폭 = 공 지름(공이 쏙 들어가 굴러갈 수 있게). 원근에 맞춰 위쪽은 좁게.
        double gutterBot = S * 1.05;
        double gutterTop = gutterBot * (laneHalfTop / laneHalfBot);
        double alleyHalfTop = laneHalfTop + gutterTop;
        double alleyHalfBot = laneHalfBot + gutterBot;

        // 표준 10핀 삼각(뒤=위 4핀 … 헤드핀=아래 1핀). 촘촘히 놓아 연쇄가 잘 일어나게.
        double hGap = S * 0.72;
        double vGap = S * 0.62;
        double backY = topY + S * 0.95;   // 뒤 열(4핀) 중심 y
        var pins = new List<Vector2>();
        int[] counts = { 4, 3, 2, 1 };
        for (int row = 0; row < counts.Length; row++)
        {
            int k = counts[row];
            double y = backY + row * vGap;
            for (int i = 0; i < k; i++)
            {
                double x = cx + (i - (k - 1) / 2.0) * hGap;
                pins.Add(new Vector2(x, y));
            }
        }
        double headY = backY + (counts.Length - 1) * vGap;
        double deckBotY = headY + S * 0.5;
        double arrowsY = headY + (foulY - headY) * 0.42;
        var ballStart = new Vector2(cx, foulY + S * 0.58); // 파울선 뒤(투구 준비 구역)에 놓는다

        return new BowlingLayout
        {
            CenterX = cx, TopY = topY, BotY = botY, FoulY = foulY,
            LaneHalfTop = laneHalfTop, LaneHalfBot = laneHalfBot,
            AlleyHalfTop = alleyHalfTop, AlleyHalfBot = alleyHalfBot,
            DeckBotY = deckBotY, ArrowsY = arrowsY,
            // 뒷줄 핀 중심을 지난 뒤 회수한다. 공이 뒷줄에 충돌할 기회는 남기되,
            // 핀덱 벽에서 되튀며 다시 핀을 쓸어버리는 상황은 막는다.
            BallExitY = backY - S * 0.15,
            BallStart = ballStart, PinCenters = pins,
        };
    }

    /// <summary>원근 선형 보간(위 top → 아래 bot).</summary>
    private double LerpAt(double top, double bot, double y)
    {
        double denom = _bl.BotY - _bl.TopY;
        double t = denom <= 0 ? 0 : (y - _bl.TopY) / denom;
        return top + (bot - top) * Math.Clamp(t, 0, 1);
    }

    /// <summary>y 에서 레인(나무 바닥)의 반폭.</summary>
    private double LaneHalfAt(double y) => LerpAt(_bl.LaneHalfTop, _bl.LaneHalfBot, y);

    /// <summary>y 에서 알리(레인+거터)의 반폭.</summary>
    private double AlleyHalfAt(double y) => LerpAt(_bl.AlleyHalfTop, _bl.AlleyHalfBot, y);

    /// <summary>y 에서 거터 홈 중심선의 x. side: -1 왼쪽 / +1 오른쪽.</summary>
    private double GutterCenterAt(double y, int side)
        => _bl.CenterX + side * (LaneHalfAt(y) + AlleyHalfAt(y)) / 2.0;

    /// <summary>핀을 레인(나무 바닥) 안 + 파울 라인 위로 가둔다. 벽에 부딪히면 감쇠 반사.</summary>
    private void ConfineToLane(SlimePhysicsEngine eng, double r)
    {
        const double wallE = 0.4;
        double half = _settings.SlimeSize / 2.0;
        Vector2 c = eng.Position + new Vector2(half, half);
        double cx = c.X, cy = c.Y;
        bool hit = false;

        double topLim = _bl.TopY + r, botLim = _bl.FoulY - r;
        if (cy < topLim) { cy = topLim; if (eng.Velocity.Y < 0) eng.Velocity = eng.Velocity.WithY(-eng.Velocity.Y * wallE); hit = true; }
        else if (cy > botLim) { cy = botLim; if (eng.Velocity.Y > 0) eng.Velocity = eng.Velocity.WithY(-eng.Velocity.Y * wallE); hit = true; }

        double lh = Math.Max(r, LaneHalfAt(cy) - r);
        double leftLim = _bl.CenterX - lh, rightLim = _bl.CenterX + lh;
        if (cx < leftLim) { cx = leftLim; if (eng.Velocity.X < 0) eng.Velocity = eng.Velocity.WithX(-eng.Velocity.X * wallE); hit = true; }
        else if (cx > rightLim) { cx = rightLim; if (eng.Velocity.X > 0) eng.Velocity = eng.Velocity.WithX(-eng.Velocity.X * wallE); hit = true; }

        if (hit) eng.Position = new Vector2(cx - half, cy - half);
    }

    /// <summary>공 전용: 파울선·뒷벽 가둠 + 거터 진입/주행 처리.</summary>
    private void UpdateBallLane(double rBall)
    {
        double half = _settings.SlimeSize / 2.0;
        Vector2 c = _physics.Position + new Vector2(half, half);
        double bx = c.X, by = c.Y;

        // ── 세로: 파울선 뒤(투구 준비 구역)까지 자유롭게 오갈 수 있고, 핀 뒤 벽만 넘지 못한다 ──
        double topLim = _bl.TopY + rBall, botLim = _bl.BotY - rBall;
        if (by < topLim)
        {
            by = topLim;
            if (_ballLaunched)
            {
                HideBallAtEnd();
            }
            else if (_physics.Velocity.Y < 0)
            {
                _physics.Velocity = _physics.Velocity.WithY(0);
            }
        }
        else if (by > botLim)
        {
            by = botLim;
            if (_physics.Velocity.Y > 0) _physics.Velocity = _physics.Velocity.WithY(0);
        }

        // ── 가로: 레인 가장자리를 넘으면 거터로 떨어진다(한 번 빠지면 복귀 불가) ──
        // 거터는 파울선 너머(레인 위)에만 있다. 준비 구역에서는 빠지지 않는다.
        if (_ballGutterSide == 0 && by < _bl.FoulY && Math.Abs(bx - _bl.CenterX) > LaneHalfAt(by))
        {
            _ballGutterSide = bx < _bl.CenterX ? -1 : +1;
            _audio.Play(ImpactTier.Bonk, 0.35);   // 거터에 툭 떨어지는 소리
        }

        if (_ballGutterSide != 0)
        {
            // 거터 주행: 좌우로는 못 움직이고 홈을 따라 앞으로만 굴러간다.
            bx = GutterCenterAt(by, _ballGutterSide);
            _physics.Velocity = _physics.Velocity.WithX(0);
            _physics.AngularVelocity = 0;
        }
        else
        {
            // 레인 위: 알리(레인+거터) 밖으로는 절대 못 나간다(안전망)
            double ah = Math.Max(rBall, AlleyHalfAt(by) - rBall);
            bx = Math.Clamp(bx, _bl.CenterX - ah, _bl.CenterX + ah);
        }

        _physics.Position = new Vector2(bx - half, by - half);
    }

    /// <summary>핀덱 뒤로 진행한 공을 숨기고, 핀 정리 뒤 다음 투구 위치에서만 다시 보이게 한다.</summary>
    private void HideBallAtEnd()
    {
        if (_ballHiddenForReset) return;
        _ballReachedEnd = true;
        _ballHiddenForReset = true;
        _physics.Velocity = Vector2.Zero;
        _physics.AngularVelocity = 0;
        _physics.SurfaceSpin = 0;
        Hide();
    }

    /// <summary>드래그/잡기로 공을 옮길 때: 레인 위 + 파울선 위로만 놓을 수 있게 클램프.</summary>
    private void ClampBallForPlacement()
    {
        double half = _settings.SlimeSize / 2.0;
        double r = _settings.SlimeSize * 0.42;
        Vector2 c = _physics.Position + new Vector2(half, half);
        double by = Math.Clamp(c.Y, _bl.TopY + r, _bl.BotY - r);
        double lh = Math.Max(r, LaneHalfAt(by) - r);
        double bx = Math.Clamp(c.X, _bl.CenterX - lh, _bl.CenterX + lh);
        _physics.Position = new Vector2(bx - half, by - half);
    }

    // ── 볼링 물리 루프(공-핀·핀-핀 충돌; 공이 무거워 핀이 튕겨나간다) ──
    private void StartBowlingLoop()
    {
        if (_bowlingActive || _pins.Count == 0) return;
        _bowlingActive = true;
        _bowlingLastTime = Now;
        CompositionTarget.Rendering += OnBowlingTick;
    }

    private void StopBowlingLoop()
    {
        if (!_bowlingActive) return;
        _bowlingActive = false;
        CompositionTarget.Rendering -= OnBowlingTick;
    }

    private void OnBowlingTick(object? sender, EventArgs e)
    {
        double now = Now;
        double dt = now - _bowlingLastTime;
        _bowlingLastTime = now;
        if (dt <= 0) return;
        if (dt > _settings.MaxFrameDeltaSeconds) dt = _settings.MaxFrameDeltaSeconds;

        double rBall = _settings.SlimeSize * 0.42;
        double rPin = PinRadius;

        double ballCenterY = _physics.Position.Y + _settings.SlimeSize / 2.0;
        if (_ballLaunched && !_ballHiddenForReset && ballCenterY <= _bl.BallExitY)
            HideBallAtEnd();

        foreach (var p in _pins)
        {
            p.Physics.Update(dt);
            // 레인은 기름칠(저마찰)이라 핀에는 별도 감쇠를 줘 금방 자리를 잡게 한다.
            p.Physics.Velocity *= Math.Exp(-3.0 * dt);
            ConfineToLane(p.Physics, rPin);
        }

        if (!_ballHiddenForReset)
        {
            // 공이 한 프레임에 많이 움직이면 핀을 뚫고 지나갈 수 있다(터널링).
            // 지난 프레임 위치에서 현재 위치까지 잘게 나눠 충돌을 검사한다.
            Vector2 curBall = _physics.Position;
            double travel = (curBall - _ballPrevPos).Length;
            double maxStep = (rBall + rPin) * 0.5;
            int steps = _ballPrevInit && travel > maxStep
                ? Math.Min(16, (int)Math.Ceiling(travel / maxStep))
                : 1;
            for (int s = 1; s <= steps; s++)
            {
                if (steps > 1)
                    _physics.Position = _ballPrevPos + (curBall - _ballPrevPos) * (s / (double)steps);
                ResolveBowlingCollisions(rBall, rPin);
            }
        }
        _ballPrevPos = _physics.Position;
        _ballPrevInit = true;

        foreach (var p in _pins) p.ApplyPosition();

        // 공: 파울선·뒷벽 가둠 + 거터 주행(충돌로 밀린 뒤에도 다시 적용)
        UpdateBallLane(rBall);
        ApplyWindowPosition();

        if (_banner != null && now >= _bannerUntil)
        {
            _banner = null;
            UpdateHud();
        }

        bool ballMoving = !_physics.IsAtRest;
        bool pinsMoving = false;
        foreach (var p in _pins) if (!p.Physics.IsAtRest) { pinsMoving = true; break; }

        // 투구 종료 판정: 던져진 공이 레인 끝에 닿았거나 멈췄고, 핀도 다 정리되면
        // 잠깐 여운을 준 뒤 점수 집계 → 다음 투구/프레임 준비(공은 시작점으로).
        if (_ballLaunched && !_gameOver && _finishThrowAt <= 0
            && (_ballReachedEnd || !ballMoving) && !pinsMoving)
        {
            _finishThrowAt = now + 0.8;
        }
        if (_finishThrowAt > 0 && now >= _finishThrowAt)
        {
            _finishThrowAt = 0;
            FinishThrow();
            return; // 이번 프레임은 여기서 마무리(위치는 FinishThrow 가 정리)
        }

        // 공·핀 모두 멈추고 대기 중인 처리도 없으면 루프 정지(유휴 CPU 0)
        bool pending = _finishThrowAt > 0 || (_banner != null && now < _bannerUntil);
        if (!ballMoving && !pinsMoving && !pending) StopBowlingLoop();
    }

    /// <summary>핀 충돌 반경 — 리디자인 전과 같은 연쇄 충돌 감각을 유지하는 물리 폭.</summary>
    private double PinRadius =>
        _settings.SlimeSize * PinWindow.BoxFactor
        * (Skins.PinSkin.CollisionWidth / Skins.PinSkin.Box) / 2.0;

    /// <summary>공(무거움)+핀(가벼움) 원-원 충돌. 핀은 맞으면 넘어지고 다른 핀도 쓰러뜨린다(연쇄).</summary>
    private void ResolveBowlingCollisions(double rBall, double rPin)
    {
        double half = _settings.SlimeSize / 2.0;
        var halfV = new Vector2(half, half);
        const double ballMass = 3.0, pinMass = 1.0;
        const double knockSpeed = 70.0;
        double maxPinSpeed = _settings.SlimeSize * 13.0; // 핀 튕김 속도 상한(과도한 비산 방지)

        int n = _pins.Count + 1; // 0 = 공, 1.. = 핀
        var eng = new SlimePhysicsEngine[n];
        var rad = new double[n];
        var invM = new double[n];
        eng[0] = _physics; rad[0] = rBall; invM[0] = 1.0 / ballMass;
        for (int i = 0; i < _pins.Count; i++)
        {
            eng[i + 1] = _pins[i].Physics; rad[i + 1] = rPin; invM[i + 1] = 1.0 / pinMass;
        }

        bool ballChanged = false;
        int newlyKnocked = 0;

        // 거터에 빠진 공은 핀에 닿지 않는다(거터볼 = 0점)
        int first = _ballGutterSide != 0 ? 1 : 0;

        for (int i = first; i < n; i++)
        for (int j = i + 1; j < n; j++)
        {
            var a = eng[i]; var b = eng[j];
            Vector2 ca = a.Position + halfV, cb = b.Position + halfV;
            Vector2 d = cb - ca;
            double dist = d.Length;
            double minDist = rad[i] + rad[j];
            if (dist <= 1e-4 || dist >= minDist) continue;

            Vector2 no = d / dist;
            double overlap = minDist - dist;
            double invSum = invM[i] + invM[j];
            // 질량 반비례로 겹침 분리
            a.Position -= no * (overlap * (invM[i] / invSum));
            b.Position += no * (overlap * (invM[j] / invSum));
            a.SetPositionClamped(a.Position);
            b.SetPositionClamped(b.Position);

            Vector2 rv = b.Velocity - a.Velocity;
            double vn = rv.X * no.X + rv.Y * no.Y;
            if (vn < 0)
            {
                const double e = 0.42; // 핀은 통 튕기지 않고 툭
                double jimp = -(1 + e) * vn / invSum;
                a.Velocity -= no * (jimp * invM[i]);
                b.Velocity += no * (jimp * invM[j]);

                // 핀 속도 상한(공은 제외 — 공은 뚫고 지나가야 한다)
                if (i != 0) a.Velocity = a.Velocity.ClampLength(maxPinSpeed);
                if (j != 0) b.Velocity = b.Velocity.ClampLength(maxPinSpeed);

                if (i == 0) ballChanged = true;
                if (KnockIfPin(i, eng[i], knockSpeed)) newlyKnocked++;
                if (KnockIfPin(j, eng[j], knockSpeed)) newlyKnocked++;
            }
        }

        if (newlyKnocked > 0) _audio.Play(ImpactTier.Bonk, 0.55);
        if (ballChanged) EnsureRendering();
    }

    /// <summary>인덱스가 핀이고 충분히 빠르면 넘어뜨린다. 새로 넘어졌으면 true.</summary>
    private bool KnockIfPin(int idx, SlimePhysicsEngine engine, double knockSpeed)
    {
        if (idx == 0) return false; // 공은 넘어지지 않음
        var pin = _pins[idx - 1];
        if (pin.Knocked) return false;
        if (engine.Velocity.Length < knockSpeed) return false;
        int dir = engine.Velocity.X >= 0 ? 1 : -1;
        pin.Knock(dir);
        return true;
    }

    private void ShowBall()
    {
        if (_settings.SlimeVisible) Show();
        ApplyWindowPosition();
    }

    private void LoseBall()
    {
        _ownsBall = false;
        Hide();
        StopRendering();
    }

    /// <summary>핸드오프 전송 후: 공은 다른 PC로 갔으므로 숨기고 유휴. 소유권은 결과 대기.</summary>
    private void HideBallForHandoff()
    {
        Hide();
        StopRendering();
        StartHandoffWatchdog();
    }

    // 넘기는 동안에는 렌더 루프를 멈추므로(유휴), 프레임 기반으로는 응답 없음을 감지할 수 없다.
    // 결과 메시지가 유실되거나 서버가 ERROR 로만 답하는 경우를 대비한 타이머 안전망.
    private System.Windows.Threading.DispatcherTimer? _handoffWatchdog;

    private void StartHandoffWatchdog()
    {
        _handoffWatchdog ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(6),
        };
        _handoffWatchdog.Tick -= OnHandoffWatchdogTick;
        _handoffWatchdog.Tick += OnHandoffWatchdogTick;
        _handoffWatchdog.Stop();
        _handoffWatchdog.Start();
    }

    private void OnHandoffWatchdogTick(object? sender, EventArgs e)
    {
        _handoffWatchdog?.Stop();
        if (_shuttingDown || _coord == null || !_coord.HasPendingOut) return;

        // 아직도 결과가 안 왔다 → 공을 잃어버린 상태. 회수해서 계속 쓸 수 있게 한다.
        if (_coord.CheckPendingTimeout(Now) == BallHandoffCoordinator.ResultKind.Reflected)
        {
            Logger.Info("Handoff watchdog fired — ball recovered locally.");
            _ownsBall = true;
            ShowBall();
            ApplyWindowPosition();
            EnsureRendering();
        }
    }

    private void StopHandoffWatchdog()
    {
        if (_handoffWatchdog == null) return;
        _handoffWatchdog.Stop();
        _handoffWatchdog.Tick -= OnHandoffWatchdogTick;
    }

    // ── 정리 ────────────────────────────────────────────────
    /// <summary>종료 절차가 시작됐는가. 늦게 도착한 릴레이 콜백이 닫힌 창을 건드리지 않게 한다.</summary>
    private bool _shuttingDown;

    public void ShutdownCleanup()
    {
        _shuttingDown = true;
        StopRendering();
        ToastWindow.CloseAll();
        _relay?.Dispose();
        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, CatchHotkeyId);
            UnregisterHotKey(_hwnd, HideHotkeyId);
        }
        RemoveMouseTrigger();
        _hwndSource?.RemoveHook(WndProc);
        _settings.PropertyChanged -= OnSettingsChanged;
        _monitors.LayoutChanged -= OnMonitorLayoutChanged;
        MouseLeftButtonDown -= OnMouseLeftButtonDown;
        MouseMove -= OnMouseMove;
        MouseLeftButtonUp -= OnMouseLeftButtonUp;

        ClearExtraBalls();
        ClearHoops();
        ExitBowling();
        try { _aimOverlay?.Close(); } catch { }
        _aimOverlay = null;
        _overlay?.ShutdownCleanup();
        _overlay?.Close();
        _overlay = null;
        _hitTextOverlay?.ShutdownCleanup();
        _hitTextOverlay?.Close();
        _hitTextOverlay = null;
        _audio.Dispose();
    }
}
