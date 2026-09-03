using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ThrowMe.Models;
using ThrowMe.Physics;

namespace ThrowMe.Views;

/// <summary>
/// 슬라임이 스스로 움직이는 기능(<see cref="AutoMoveMode"/>).
///
/// 무한 튕기기와의 차이는 "느리고 끊임없이" 다. 튕겨 다니는 게 아니라 기어다닌다.
/// 속도를 직접 넣지 않고 엔진의 <see cref="SlimePhysicsEngine.Propulsion"/> 에 가속도를 주면,
/// 마찰과 균형을 이뤄 목표 속도에서 저절로 멎는다. 벽 충돌·랜덤 반사·모니터 이동은
/// 기존 물리가 그대로 처리한다.
/// </summary>
public partial class SlimeWindow
{
    // ── 클릭 통과 ───────────────────────────────────────────
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT_AM = 0x20;

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLongAm(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLongAm(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINTAM p);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINTAM { public int X, Y; }

    private const int SM_CXCURSOR = 13;

    private bool _clickThrough;

    /// <summary>
    /// 창을 클릭 통과로 만든다(작업표시줄·커서 따라가기 모드).
    /// 이 모드에서는 슬라임이 작업 위를 돌아다니므로, 클릭을 받으면 뒤에 있는 창의 조작을 삼킨다.
    /// 자동 이동 중에는 잡기 단축키도 무시한다 — 스스로 걷는 중에 커서로 끌어오면 걷기가 깨진다.
    /// </summary>
    private void SetClickThrough(bool on)
    {
        if (_hwnd == IntPtr.Zero || _clickThrough == on) return;
        _clickThrough = on;
        int ex = GetWindowLongAm(_hwnd, GWL_EXSTYLE);
        SetWindowLongAm(_hwnd, GWL_EXSTYLE, on ? ex | WS_EX_TRANSPARENT_AM : ex & ~WS_EX_TRANSPARENT_AM);
    }

    // ── 상태 ────────────────────────────────────────────────
    /// <summary>지금 향하고 있는 방향(라디안). 시간이 지나며 조금씩 흔들린다.</summary>
    private double _autoHeading;

    /// <summary>다음에 방향을 틀 시각(초).</summary>
    private double _autoTurnAt;

    private readonly Random _autoRng = new();

    /// <summary>방향을 트는 간격(초). 너무 짧으면 부들거리고, 길면 직선으로만 간다.</summary>
    private const double TurnMinSec = 1.2, TurnMaxSec = 3.5;

    /// <summary>한 번에 트는 최대 각도(deg).</summary>
    private const double TurnMaxDeg = 55.0;

    /// <summary>커서 따라가기에서 커서 오른쪽 아래로 얼마나 떨어져 설 것인가(공 지름 대비).</summary>
    private const double CursorOffsetFactor = 0.55;

    // ── 걸음(gait) ──────────────────────────────────────────
    // 일정 속도로 미끄러지면 기어가는 게 아니라 떠다니는 것처럼 보인다.
    // 사람이 한 발 내딛고 멈추듯, 모았다가 쭉 뻗고 다시 멈추기를 반복한다.

    /// <summary>
    /// 기준 속도(px/s)에서 한 걸음에 나아가는 거리(공 지름 대비).
    /// 기어다니기·작업표시줄 걷기의 걸음 주기는 이 기준 속도로 고정하고, 설정 속도를 올리면
    /// <b>걸음은 그대로 두고 한 걸음 거리만 늘어난다</b>(뻗는 순간 더 멀리 나간다).
    /// </summary>
    private const double StepDistanceFactor = 0.22;

    /// <summary>걸음 주기를 정하는 기준 속도(px/s). 설정 속도와 무관하게 이 빠르기로 발을 놓는다.</summary>
    private const double GaitBaseSpeed = 17.0;

    /// <summary>걸음 주기의 하한·상한(초).</summary>
    private const double StepPeriodMin = 0.22, StepPeriodMax = 3.0;

    /// <summary>주어진 속도에 맞는 걸음 주기. 빠를수록 짧아진다. 커서 따라가기가 급할 때 쓴다.</summary>
    private double StepPeriodFor(double speed)
    {
        double stepDist = _settings.SlimeSize * StepDistanceFactor;
        return Math.Clamp(stepDist / Math.Max(1.0, speed), StepPeriodMin, StepPeriodMax);
    }

    /// <summary>기어다니기·작업표시줄 걷기의 고정 걸음 주기(설정 속도와 무관).</summary>
    private double FixedStepPeriod => StepPeriodFor(GaitBaseSpeed * _settings.DisplayScale);

    /// <summary>주기 안에서 실제로 밀고 나가는 구간(0~1). 나머지는 모으거나 멈춰 있다.</summary>
    private const double LungeFrom = 0.30, LungeTo = 0.68;

    /// <summary>뻗는 구간이 주기에서 차지하는 평균 비율. 설정 속도를 이 값으로 나눠 최고 속도를 정한다.</summary>
    private static readonly double LungeDuty = (LungeTo - LungeFrom) * 2.0 / Math.PI;

    /// <summary>걸음 주기 안의 위치(0~1).</summary>
    private double _stepPhase;

    /// <summary>이번 프레임의 뻗기 세기(0~1). 뻗는 구간에서만 0 보다 크다.</summary>
    private double _lunge;

    /// <summary>
    /// 지금 바라보는 쪽. <b>걸음이 끝날 때만</b> 바꾼다.
    /// 매 프레임 진행 방향으로 판정하면, 방향이 위아래로 조금만 흔들려도 좌우 그림이
    /// 번갈아 나와 제자리에서 떠는 것처럼 보인다.
    /// </summary>
    private bool _faceRight = true;

    /// <summary>이보다 가로 성분이 작으면(거의 수직 이동) 바라보는 쪽을 바꾸지 않는다.</summary>
    private const double FacingDeadzone = 0.25;

    /// <summary>바라보는 쪽을 갱신한다. 가로 성분이 뚜렷할 때만 바꾼다.</summary>
    private void UpdateFacing(double dx)
    {
        if (Math.Abs(dx) > FacingDeadzone) _faceRight = dx > 0;
    }

    /// <summary>뻗기 직전 몸을 모으는 정도(0~1).</summary>
    private static double GatherCurve(double ph)
    {
        if (ph >= LungeFrom) return 0;
        return Math.Sin(ph / LungeFrom * Math.PI * 0.5);   // 뻗기 직전에 최대
    }

    /// <summary>뻗기 세기. 구간 밖은 0, 안에서는 부드럽게 솟았다 가라앉는다.</summary>
    private static double LungeCurve(double ph)
    {
        if (ph < LungeFrom || ph > LungeTo) return 0;
        return Math.Sin((ph - LungeFrom) / (LungeTo - LungeFrom) * Math.PI);
    }

    /// <summary>걸음 주기를 진행시킨다. 한 걸음이 끝나면 true.</summary>
    private bool AdvanceStep(double dt, double period)
    {
        bool finished = false;
        _stepPhase += dt / Math.Max(0.05, period);
        while (_stepPhase >= 1.0) { _stepPhase -= 1.0; finished = true; }
        _lunge = LungeCurve(_stepPhase);
        return finished;
    }

    /// <summary>
    /// 기어다닐 때의 형태를 만든다 — 바닥에 눌린 납작한 몸 + 걸음에 맞춘 꼬물거림.
    /// 모을 때 세로로 볼록해지고, 뻗을 때 진행 방향으로 쭉 늘어난다.
    /// </summary>
    /// <param name="seqT">컷 순서 안의 위치(0~1). 스킨이 그림을 직접 쓸 때만 의미가 있다.</param>
    private void ApplyCrawlShape(double headingRad, double lungeAmount, double seqT)
    {
        if (_animation == null) return;

        // 스킨이 실루엣을 직접 그릴 수 있으면 그쪽에 맡긴다 — 스케일 변형은 찌그러진 타원이
        // 될 뿐이라 바닥에 눌린 돔과 꼬리가 나오지 않는다. 둘을 겹치면 이중으로 뭉개진다.
        if (SkinHost.Content is Skins.ISkinCrawl crawl)
        {
            // 크기 1·각도 0 으로 못박는다. null 로 두면 속도 기반 로직이 살아나
            // 진행 방향으로 몸 전체를 회전시켜, 평평해야 할 바닥이 비스듬해진다.
            _animation.CrawlShape = (1.0, 1.0, 0.0);
            crawl.SetCrawlPose(seqT, _faceRight);
            return;
        }

        double soft = 0.45 + 0.55 * Math.Clamp(_settings.Softness, 0, 1);
        double gather = GatherCurve(_stepPhase);

        // 눌린 기본형: 가로로 퍼지고 세로로 눌린다(그림의 돔 모양).
        double x = (1.0 + 0.16 * soft) * (1.0 - 0.10 * soft * gather + 0.22 * soft * lungeAmount);
        double y = (1.0 - 0.20 * soft) * (1.0 + 0.14 * soft * gather - 0.18 * soft * lungeAmount);

        // 진행 방향으로 살짝 기운다. 위아래로 갈 때 몸이 뒤집히지 않도록 크게 돌리지 않는다.
        double deg = headingRad * 180.0 / Math.PI;
        while (deg > 90) deg -= 180;
        while (deg < -90) deg += 180;
        double lean = Math.Clamp(deg, -12, 12) * (0.35 + 0.65 * lungeAmount);

        _animation.CrawlShape = (x, y, lean);
    }

    private bool AutoMoveOn => _settings.AutoMove != AutoMoveMode.Off;

    /// <summary>자동 이동이 실제로 돌아야 하는 상황인가.</summary>
    private bool AutoMoveActive =>
        AutoMoveOn
        && !_isDragging
        && !_settings.Paused
        && _settings.SlimeVisible
        && !BowlingOn
        // 슬라임(젤리) 전용. 설정에서도 막지만, 저장 파일이 어긋난 경우를 대비해 여기서도 확인한다.
        && _settings.Skin == SlimeSkinKind.Jelly;

    /// <summary>설정이 바뀌면 창 속성·중력을 다시 맞춘다.</summary>
    private void ApplyAutoMove()
    {
        // 작업표시줄 모드는 중력이 필요하고, 커서 따라가기·기어다니기는 무중력이라야 한다.
        UpdateSkinBehavior();
        // 작업표시줄과 커서 따라가기는 작업을 가리는 자리에 있으므로 클릭을 통과시킨다.
        SetClickThrough(_settings.AutoMove is AutoMoveMode.Taskbar or AutoMoveMode.CursorFollow);
        ApplyCustomImage();  // 자동 이동 중에는 커스텀 이미지를 숨긴다(걸음 자세와 안 맞음)
        if (AutoMoveOn)
        {
            _autoHeading = _autoRng.NextDouble() * Math.PI * 2.0;
            _autoTurnAt = 0;
            // 다른 테마에서 회전(스핀)을 하고 돌아왔더라도, 자동 이동을 켜면 바닥이 아래로
            // 오도록 회전 상태를 초기화한다. 렌더 회전각 = 걸음각(0) + 물리 스핀각 이라,
            // 남아 있는 스핀각을 지우지 않으면 기울어진 채 걷는다.
            _physics.SpinAngle = 0;
            _physics.AngularVelocity = 0;
            _physics.SurfaceSpin = 0;
            EnsureRendering();
        }
        else _physics.Propulsion = Vector2.Zero;
    }

    /// <summary>자동 이동 안내를 붙잡아 두는 키.</summary>
    private const string AutoMoveNoticeKey = "auto-move";

    /// <summary>
    /// 클릭 통과 모드에서는 슬라임을 눌러도 반응이 없고 우클릭 메뉴도 열리지 않는다.
    /// 설정으로 돌아오는 길을 켜져 있는 동안 계속 띄워 둔다 — 트레이 아이콘은 Windows 11 에서
    /// 기본적으로 숨김 영역에 들어가 있어 못 찾는 사람이 많다.
    /// </summary>
    private void UpdateAutoMoveNotice()
    {
        bool clickThroughMode = _settings.AutoMove is AutoMoveMode.Taskbar or AutoMoveMode.CursorFollow;
        if (!clickThroughMode)
        {
            ToastWindow.CloseSticky(AutoMoveNoticeKey);
            return;
        }

        string combo = Services.HotkeyText.Chord(_settings.OpenSettingsHoldVk, _settings.OpenSettingsVk);
        string what = _settings.AutoMove == AutoMoveMode.Taskbar ? "작업표시줄 걷기" : "커서 따라가기";
        ToastWindow.ShowSticky(AutoMoveNoticeKey, $"{what}를 켰어요",
            $"이 동안에는 슬라임을 클릭할 수 없습니다(뒤 창을 가리지 않으려고요). " +
            $"설정은 {combo} 또는 트레이 아이콘으로 열 수 있어요.");
    }

    /// <summary>자동 이동을 가둘 사각형. 지정한 모니터가 없으면 null(전체 허용).</summary>
    private Rect? AutoMoveArea()
    {
        string key = _settings.AutoMoveMonitor;
        if (string.IsNullOrWhiteSpace(key)) return null;
        foreach (var b in _monitors.MonitorBounds)
            if (MonitorKey(b) == key) return b;
        return null;   // 그 모니터가 사라졌다 → 전체를 쓴다
    }

    /// <summary>설정 창이 모니터 목록을 그릴 때 쓴다.</summary>
    public IReadOnlyList<Rect> MonitorBoundsForSettings => _monitors.MonitorBounds;

    /// <summary>모니터를 식별하는 문자열. 장치 이름 대신 해상도·좌표를 쓴다.</summary>
    internal static string MonitorKey(Rect b) =>
        $"{(int)b.Width}x{(int)b.Height}@{(int)b.Left},{(int)b.Top}";

    /// <summary>
    /// 벽에 부딪혀 엔진이 추진 방향을 꺾었으면, 그 방향을 우리 heading 에도 반영한다.
    /// 이걸 안 하면 다음 프레임에 <see cref="TickRoam"/> 이 옛 heading 으로 추진력을 다시 덮어써
    /// 엔진의 반사가 무의미해지고, 슬라임이 벽에 붙어 미끄러지기만 한다.
    /// </summary>
    private void OnAutoMoveCollision(Vector2 normal)
    {
        if (!AutoMoveActive) return;

        if (_settings.AutoMove == AutoMoveMode.Taskbar)
        {
            // 바닥에는 매 프레임 닿아 있다. 그때마다 방향을 다시 읽으면, 속도 제어기가
            // 잠깐 반대로 미는 순간까지 방향 전환으로 오해해 제자리에서 떤다(실측 3.8px/s).
            // 좌우 벽에 부딪혔을 때만 돌아선다.
            if (Math.Abs(normal.X) > 0.5) _autoHeading = normal.X > 0 ? 0 : Math.PI;
            return;
        }

        if (_settings.AutoMove == AutoMoveMode.CursorFollow) return;

        Vector2 p = _physics.Propulsion;
        if (p.LengthSquared > 1e-9) _autoHeading = Math.Atan2(p.Y, p.X);
    }

    /// <summary>매 프레임 추진력을 갱신한다. <c>_physics.Update</c> 직전에 부른다.</summary>
    private void TickAutoMove(double dt)
    {
        if (!AutoMoveActive)
        {
            _physics.Propulsion = Vector2.Zero;
            _physics.AutoMoving = false;
            if (_animation != null) _animation.CrawlShape = null;      // 평소 형태로 되돌린다
            (SkinHost.Content as Skins.ISkinCrawl)?.ClearCrawlPose();  // 스킨 도형도 원래대로
            return;
        }
        _physics.AutoMoving = true;
        // 자동 이동 중에는 스핀 회전이 남지 않게 유지한다(기울어진 채 걷지 않도록).
        _physics.SpinAngle = 0;
        _physics.AngularVelocity = 0;

        // 목표 속도(px/s). 화면 배율을 곱해 어느 화면에서도 같은 빠르기로 보이게 한다.
        double speed = _settings.AutoMoveSpeed * _settings.DisplayScale;

        switch (_settings.AutoMove)
        {
            case AutoMoveMode.CursorFollow: TickCursorFollow(dt, speed); break;
            case AutoMoveMode.Taskbar: TickTaskbarWalk(dt, speed); break;
            default: TickRoam(dt, speed); break;
        }
    }

    /// <summary>
    /// 목표 속도로 수렴시키는 추진력을 구한다.
    ///
    /// 마찰만 보고 <c>a = v × 마찰</c> 로 역산하면 실제 속도가 설정값보다 느려진다 —
    /// 바닥에 붙어 있을 때 구름 마찰이 따로 더 걸리기 때문이다(실측: 18 설정에 10.9px/s).
    /// 목표 속도와의 차이에 비례해 밀면 마찰이 무엇이든 설정값에 맞는다.
    /// </summary>
    private Vector2 SteerTo(Vector2 wantVelocity)
    {
        const double gain = 6.0;   // 1/s. 높이면 빨리 붙고, 너무 높으면 출렁인다.
        return (wantVelocity - _physics.Velocity) * gain;
    }

    /// <summary>화면을 느릿느릿 기어다닌다.</summary>
    private void TickRoam(double dt, double speed)
    {
        // 방향은 걸음과 걸음 사이(멈춰 있을 때)에만 튼다 — 뻗는 도중에 꺾이면 미끄러져 보인다.
        // 걸음 주기는 고정. 속도를 올리면 같은 주기 안에서 더 멀리 나간다(보폭만 늘어남).
        bool stepDone = AdvanceStep(dt, FixedStepPeriod);
        double now = Now;
        if (stepDone && now >= _autoTurnAt)
        {
            _autoTurnAt = now + TurnMinSec + _autoRng.NextDouble() * (TurnMaxSec - TurnMinSec);
            double turn = (_autoRng.NextDouble() * 2 - 1) * TurnMaxDeg * Math.PI / 180.0;
            _autoHeading += turn;
        }

        Vector2 dir = new(Math.Cos(_autoHeading), Math.Sin(_autoHeading));

        // 지정한 모니터 밖으로 나갔거나 나가려 하면 안쪽으로 방향을 돌린다.
        // 물리의 벽으로 막지 않는 이유는, 던지기는 어느 모니터로든 갈 수 있어야 하기 때문이다.
        Rect? area = AutoMoveArea();
        if (area is { } r)
        {
            double s = _settings.SlimeSize;
            Vector2 c = _physics.Position + new Vector2(s / 2, s / 2);
            double margin = s * 0.75;
            Vector2 pull = Vector2.Zero;
            if (c.X < r.Left + margin) pull += new Vector2(1, 0);
            else if (c.X > r.Right - margin) pull += new Vector2(-1, 0);
            if (c.Y < r.Top + margin) pull += new Vector2(0, 1);
            else if (c.Y > r.Bottom - margin) pull += new Vector2(0, -1);

            if (pull.LengthSquared > 1e-9)
            {
                dir = (dir + pull.Normalized() * 2.0).Normalized();
                _autoHeading = Math.Atan2(dir.Y, dir.X);
                _autoTurnAt = now + TurnMinSec; // 경계에서 곧바로 다시 틀지 않게
            }
        }

        // 뻗는 구간에만 민다. 나머지 구간에서는 목표 속도가 0 이라 제자리에 멎는다.
        // 평균이 설정 속도가 되도록 최고 속도를 duty 로 나눠 올린다.
        if (stepDone) UpdateFacing(dir.X);   // 걸음이 끝날 때만 바라보는 쪽을 바꾼다
        _physics.Propulsion = SteerTo(dir * (speed / LungeDuty * _lunge));
        ApplyCrawlShape(_autoHeading, _lunge, _stepPhase);
    }

    /// <summary>중력으로 화면 아래(작업표시줄 위)에 붙어 좌우로만 걷는다.</summary>
    private void TickTaskbarWalk(double dt, double speed)
    {
        // 걸음 주기는 고정. 속도는 한 걸음 거리에만 반영된다.
        bool stepDone = AdvanceStep(dt, FixedStepPeriod);
        double now = Now;
        if (stepDone && now >= _autoTurnAt)
        {
            _autoTurnAt = now + TurnMinSec * 2 + _autoRng.NextDouble() * (TurnMaxSec * 2);
            if (_autoRng.NextDouble() < 0.35) _autoHeading = _autoHeading >= 0 ? Math.PI : 0; // 가끔 방향 전환
        }

        double sign = Math.Cos(_autoHeading) >= 0 ? 1.0 : -1.0;

        Rect? area = AutoMoveArea();
        if (area is { } r)
        {
            double s = _settings.SlimeSize;
            double cx = _physics.Position.X + s / 2;
            double margin = s * 0.75;
            if (cx < r.Left + margin) { sign = 1; _autoHeading = 0; }
            else if (cx > r.Right - margin) { sign = -1; _autoHeading = Math.PI; }
        }

        // 세로는 건드리지 않는다 — 중력이 바닥에 붙여 주고, 우리는 좌우 속도만 맞춘다.
        // 뻗는 구간에만 밀어서, 한 발 내딛고 멈추기를 반복한다.
        double wantX = sign * speed / LungeDuty * _lunge;
        _physics.Propulsion = new Vector2(SteerTo(new Vector2(wantX, _physics.Velocity.Y)).X, 0);
        UpdateFacing(sign);
        ApplyCrawlShape(sign > 0 ? 0 : Math.PI, _lunge, _stepPhase);
    }

    /// <summary>커서의 오른쪽 아래를 목표로 천천히 따라간다.</summary>
    private void TickCursorFollow(double dt, double speed)
    {
        if (!GetCursorPos(out var p))
        {
            _physics.Propulsion = Vector2.Zero;
            ApplyCrawlShape(0, 0, 0);
            return;
        }

        double s = _settings.SlimeSize;
        Vector2 cursor = new(p.X, p.Y);
        Vector2 center = _physics.Position + new Vector2(s / 2, s / 2);

        if (_settings.CursorFollowStyle == CursorFollowStyle.Keyring)
        {
            TickKeyring(dt, cursor, center, s);
            return;
        }

        int cur = Math.Max(16, GetSystemMetrics(SM_CXCURSOR));
        // 커서 바로 오른쪽 아래 대각선. 커서 크기만큼 비켜서고, 공 중심이 그 자리에 오게 한다.
        Vector2 target = new(cursor.X + cur * 0.6 + s * CursorOffsetFactor,
                             cursor.Y + cur * 0.6 + s * CursorOffsetFactor);
        Vector2 delta = target - center;
        double dist = delta.Length;

        // 멀수록 걸음을 빨리 놓는다. 보폭을 늘리는 게 아니라 <b>걸음이 빨라진다</b> —
        // 같은 속도로 쭉 미끄러져 오면 따라오는 게 아니라 끌려오는 것처럼 보인다.
        // (기어다니기·작업표시줄 걷기의 고정 주기와 달리, 이 모드는 설정 속도가 주기에도 반영된다.)
        double urgency = Math.Clamp(dist / Math.Max(1.0, s * 1.5), 0.6, 5.0);
        bool stepDone = AdvanceStep(dt, StepPeriodFor(speed * urgency));

        if (dist < s * 0.25)
        {
            // 다 왔으면 멈춰 선다(제자리에서 계속 걷지 않게).
            _physics.Propulsion = SteerTo(Vector2.Zero);
            ApplyCrawlShape(0, 0, 0);
            return;
        }

        Vector2 dir = delta.Normalized();
        if (stepDone) UpdateFacing(dir.X);
        // 뻗는 구간에만 민다. 걸음마다 커서 쪽으로 한 번씩 튀어 간다.
        _physics.Propulsion = SteerTo(dir * (speed * urgency / LungeDuty * _lunge));
        ApplyCrawlShape(Math.Atan2(dir.Y, dir.X), _lunge, _stepPhase);
    }

    // ── 키링 ────────────────────────────────────────────────
    /// <summary>커서에 매다는 줄 길이(공 지름 대비).</summary>
    private const double KeyringRopeFactor = 0.85;

    /// <summary>커서가 빠를수록 뒤로 끌리는 정도(s/px). 커서 속도를 매달린 방향에 섞는 양.</summary>
    private const double KeyringTrail = 0.0035;

    /// <summary>매달린 방향이 목표 방향을 따라가는 속도(1/s). 낮을수록 크게 흔들린다.</summary>
    private const double KeyringSwing = 7.0;

    /// <summary>공이 매달릴 자리로 붙는 속도(1/s).</summary>
    private const double KeyringFollow = 16.0;

    /// <summary>지금 매달려 있는 방향(커서 기준 단위 벡터). 아래가 기본.</summary>
    private Vector2 _keyringDir = new(0, 1);

    private Vector2 _prevCursor;
    private bool _hasPrevCursor;

    /// <summary>
    /// 커서에 매달려 흔들린다. 커서를 움직이면 반대쪽으로 끌려 늘어졌다가, 멈추면 아래로 모인다.
    ///
    /// 진자를 물리로 돌려 봤지만 흔들림이 잦아들지 않았다(30초 뒤에도 73도).
    /// 줄을 힘으로 흉내 내면 멀 때 당기는 힘이 폭발해 화면 밖으로 날아가기도 했다(실측 2037px).
    /// 그래서 매달릴 자리를 직접 계산해 그쪽으로 붙인다 — 어떤 상황에서도 튀지 않는다.
    /// </summary>
    private void TickKeyring(double dt, Vector2 cursor, Vector2 center, double s)
    {
        _stepPhase = 0;
        _lunge = 0;
        _physics.Propulsion = Vector2.Zero;

        Vector2 cursorVel = Vector2.Zero;
        if (_hasPrevCursor && dt > 1e-4) cursorVel = (cursor - _prevCursor) / dt;
        _prevCursor = cursor;
        _hasPrevCursor = true;

        // 매달릴 방향: 기본은 아래. 커서가 움직이면 그 반대쪽으로 밀려 비스듬해진다.
        Vector2 want = new Vector2(0, 1) + cursorVel * -KeyringTrail;
        if (want.LengthSquared < 1e-9) want = new Vector2(0, 1);
        want = want.Normalized();

        // 방향이 천천히 따라오면서 흔들림(오버슈트)이 생긴다.
        double turn = 1.0 - Math.Exp(-KeyringSwing * dt);
        _keyringDir = (_keyringDir + (want - _keyringDir) * turn).Normalized();

        double rope = s * KeyringRopeFactor;
        Vector2 target = cursor + _keyringDir * rope;

        double follow = 1.0 - Math.Exp(-KeyringFollow * dt);
        Vector2 next = center + (target - center) * follow;

        Vector2 moved = next - center;
        _physics.SetPositionClamped(next - new Vector2(s / 2, s / 2));
        // 속도는 0 이어야 한다. 여기서 위치를 직접 옮겼는데 속도까지 넣으면
        // 엔진이 이어서 한 번 더 이동시켜, 목표를 지나치며 계속 진동한다.
        _physics.Velocity = Vector2.Zero;

        double swing = Math.Min(1.0, moved.Length / Math.Max(1.0, s * 0.08));
        if (Math.Abs(moved.X) > s * 0.01) UpdateFacing(moved.X);
        ApplyCrawlShape(0, swing, swing * 0.5);
    }
}
