using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace ThrowMe.Models;

/// <summary>
/// 물리·상호작용·시각 효과의 튜닝 값을 한곳에 모은 설정 객체.
/// 핵심 수치는 여기에서만 관리하며 코드에 하드코딩하지 않는다.
///
/// SettingsWindow 가 이 인스턴스에 직접 바인딩한다. 물리 루프가 매 프레임
/// 설정값을 읽으므로, INotifyPropertyChanged 알림 대상(사용자 조절 항목)은
/// 슬라이더/토글을 움직이는 즉시 효과가 반영된다.
/// (Topmost·Paused·표시 여부처럼 즉시 반영이 필요한 항목은
///  SlimeWindow 가 PropertyChanged 를 구독해 처리한다.)
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    // ── 물리 ────────────────────────────────────────────────
    // 엔진 튜닝 상수는 저장하지 않고 항상 코드 기본값 사용(JsonIgnore) → 파일이 옛 값으로 덮어쓰지 않음.
    /// <summary>공기저항/마찰 감쇠율(1/s). 낮을수록 멀리 오래 튄다. velocity *= exp(-Friction*dt)</summary>
    [JsonIgnore] public double Friction { get; set; } = 0.9;

    /// <summary>반발 계수(Bounce Power). 충돌 시 velocity *= Restitution. 0~1</summary>
    private double _restitution = 0.7;
    public double Restitution { get => _restitution; set => Set(ref _restitution, value); }

    /// <summary>관성 이동 최대 속도(px/s). 폭주 방지 상한.
    /// 이펙트 세기 기준이 아니라 순수 상한이다(기준은 <see cref="ImpactReferenceSpeed"/>).</summary>
    [JsonIgnore] public double MaxSpeed { get; set; } = 40000.0;

    /// <summary>던질 때 계산되는 초기 속도 상한(px/s). 쎄게 던지면 팡팡 튀도록 높게.</summary>
    [JsonIgnore] public double MaxThrowSpeed { get; set; } = 40000.0;

    /// <summary>충돌 세기·찌그러짐·표정을 0~1 로 정규화할 때 쓰는 기준 속도(px/s).
    /// MaxSpeed 와 분리해 둔다 — 상한을 올려도 이펙트가 터지는 임계는 그대로 유지되도록.</summary>
    [JsonIgnore] public double ImpactReferenceSpeed { get; set; } = 7000.0;

    /// <summary>던지기 가중치. 계산된 마우스 속도에 곱한다. 1.0 = 실제 마우스 속도 그대로.</summary>
    private double _throwPower = 1.0;
    public double ThrowPower { get => _throwPower; set => Set(ref _throwPower, value); }

    /// <summary>던지기 가중치의 최솟값(설정 슬라이더 Minimum 과 같다). 이보다 작은 값은 UI 로 만들 수 없다.</summary>
    public const double MinThrowPower = 0.3;

    /// <summary>
    /// 속도 상한 배율. 던지기 가중치를 아무리 올려도 <see cref="MaxSpeed"/>·<see cref="MaxThrowSpeed"/>
    /// 에서 잘리기 때문에 어느 지점부터는 더 세게 던져도 똑같아진다. 이 배율이 그 천장을 올린다.
    /// 테마별 상한(종이비행기·농구공)에도 함께 곱해져, 어떤 테마에서든 슬라이더가 먹는다.
    /// </summary>
    private double _speedLimitScale = 1.0;
    public double SpeedLimitScale { get => _speedLimitScale; set => Set(ref _speedLimitScale, value); }

    /// <summary>
    /// 감속 배율. 날아가는 공이 느려지는 정도(<see cref="Friction"/>)에 곱한다.
    /// 낮출수록 속도를 오래 유지해 멀리 날아가고, 높이면 금방 멈춘다.
    /// 엔진의 <c>EffFriction</c> 한 곳에서 곱하므로 테마·골대·볼링의 마찰 오버라이드에도 그대로 적용된다.
    /// </summary>
    private double _slowdownScale = 1.0;
    public double SlowdownScale { get => _slowdownScale; set => Set(ref _slowdownScale, value); }

    /// <summary>슬라이더 범위(= UI Minimum/Maximum). 이 밖의 값은 손상으로 보고 되돌린다.</summary>
    public const double MinSpeedLimitScale = 0.25, MaxSpeedLimitScale = 4.0;

    /// <summary>감속 0 = 공기저항 없음(무한 튕기기). 그래서 최솟값이 0 이다.</summary>
    public const double MinSlowdownScale = 0.0, MaxSlowdownScale = 3.0;

    /// <summary>
    /// 무한 튕기기. 켜면 감속을 0(공기저항 없음), 반발을 1.0(벽에서 힘을 잃지 않음)으로 만든다.
    ///
    /// 감속만 0 으로 해서는 무한이 되지 않는다 — 벽에 부딪힐 때마다 반발 계수만큼 잃기 때문에
    /// 결국 <see cref="StopThreshold"/> 아래로 떨어져 멈춘다. 두 값이 함께 있어야 한다.
    /// 끄면 켜기 직전의 값으로 되돌린다(종이비행기 가중치와 같은 방식).
    /// </summary>
    private bool _infiniteBounce;
    public bool InfiniteBounce
    {
        get => _infiniteBounce;
        set
        {
            if (_infiniteBounce == value) return;
            if (value)
            {
                _slowdownBeforeInfinite = SlowdownScale;
                _restitutionBeforeInfinite = Restitution;
                SlowdownScale = 0.0;
                Restitution = 1.0;
            }
            else
            {
                // 보관값이 없으면(설정 파일이 옛 버전이면) 기본값으로 돌린다.
                SlowdownScale = _slowdownBeforeInfinite >= 0 ? _slowdownBeforeInfinite : 1.0;
                Restitution = _restitutionBeforeInfinite >= 0 ? _restitutionBeforeInfinite : 0.7;
                _slowdownBeforeInfinite = -1;
                _restitutionBeforeInfinite = -1;
            }
            Set(ref _infiniteBounce, value);
        }
    }

    /// <summary>무한 튕기기를 켜기 직전의 값. 음수 = 보관값 없음.</summary>
    private double _slowdownBeforeInfinite = -1;
    public double SlowdownBeforeInfinite
    {
        get => _slowdownBeforeInfinite;
        set => _slowdownBeforeInfinite = value;
    }

    private double _restitutionBeforeInfinite = -1;
    public double RestitutionBeforeInfinite
    {
        get => _restitutionBeforeInfinite;
        set => _restitutionBeforeInfinite = value;
    }

    /// <summary>배율까지 반영한 실제 속도 상한. 코드에서는 항상 이 값을 쓴다.</summary>
    [JsonIgnore] public double EffectiveMaxSpeed => MaxSpeed * SpeedLimitScale;

    /// <summary>배율까지 반영한 실제 던지기 속도 상한.</summary>
    [JsonIgnore] public double EffectiveMaxThrowSpeed => MaxThrowSpeed * SpeedLimitScale;

    /// <summary>
    /// 설정 파일에서 읽은 값 중 <b>UI 로는 나올 수 없는</b> 값을 되살린다.
    ///
    /// 던지기 가중치가 0 이 되면 던질 때 속도가 `마우스속도 × 0 = 0` 이라 공이 손을 떠나지 않고
    /// 놓은 자리에 그대로 멈춘다. 그 값이 파일에 저장되면 재시작해도 낫지 않는다.
    /// 슬라이더 최솟값이 <see cref="MinThrowPower"/> 이므로 그보다 작은 값(0·음수·NaN)은
    /// 손상으로 보고 기본값으로 되돌린다. 정상 값은 건드리지 않는다.
    /// </summary>
    /// <summary>
    /// 모든 설정을 기본값으로 되돌린다(설정 → 일반 → `설정 초기화`).
    ///
    /// 속성을 하나씩 나열하지 않고 <b>기본값 인스턴스에서 통째로 복사</b>한다 —
    /// 나중에 설정이 추가돼도 여기를 고치지 않아도 함께 초기화된다.
    /// 각 대입이 <see cref="PropertyChanged"/> 를 타므로 창과 슬라임에 즉시 반영되고,
    /// 자동 저장으로 파일에도 곧바로 기록된다.
    ///
    /// 이 클래스가 갖고 있지 않은 것은 지우지 않는다 — 방 코드·비밀번호(relay.json)와
    /// 직접 그린 그림 파일(skins/*.png)은 그대로 남는다. 그림을 쓰던 설정만 풀린다.
    /// </summary>
    public void ResetToDefaults()
    {
        var defaults = new AppSettings();
        foreach (var p in typeof(AppSettings).GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!p.CanRead || !p.CanWrite || p.GetIndexParameters().Length > 0) continue;
            p.SetValue(this, p.GetValue(defaults));
        }

        // Dictionary 는 통째로 갈아 끼워도 자동 통보되지 않는다.
        NotifySkinImagesChanged();
        Services.Logger.Info("Settings reset to defaults by user.");
    }

    /// <returns>고친 값이 있으면 true(호출한 쪽에서 즉시 저장해 파일까지 낫게 한다).</returns>
    public bool RepairInvalidValues()
    {
        bool repairedAny = false;

        if (!(ThrowPower >= MinThrowPower))
        {
            // 종이비행기는 원래 최솟값을 쓰므로 그 값으로, 나머지는 기본값(1.0)으로 되돌린다.
            double repaired = Skin == SlimeSkinKind.PaperPlane ? MinThrowPower : 1.0;
            Services.Logger.Info($"Repaired invalid ThrowPower ({ThrowPower}) -> {repaired}.");
            ThrowPower = repaired;
            repairedAny = true;
        }

        // 종이비행기 보관값: 0 이하 = "되돌릴 값 없음". NaN·음수만 정리한다.
        if (double.IsNaN(ThrowPowerBeforePaperPlane) || ThrowPowerBeforePaperPlane < 0)
        {
            ThrowPowerBeforePaperPlane = 0;
            repairedAny = true;
        }

        // 속도 상한 배율이 0 이면 공이 아예 못 움직인다(속도가 0 으로 잘린다).
        // 감속 배율이 0 이면 영영 안 멈춘다. 둘 다 슬라이더 범위 밖은 손상으로 본다.
        if (!(SpeedLimitScale >= MinSpeedLimitScale && SpeedLimitScale <= MaxSpeedLimitScale))
        {
            Services.Logger.Info($"Repaired invalid SpeedLimitScale ({SpeedLimitScale}) -> 1.0.");
            SpeedLimitScale = 1.0;
            repairedAny = true;
        }
        if (!(SlowdownScale >= MinSlowdownScale && SlowdownScale <= MaxSlowdownScale))
        {
            Services.Logger.Info($"Repaired invalid SlowdownScale ({SlowdownScale}) -> 1.0.");
            SlowdownScale = 1.0;
            repairedAny = true;
        }

        // 잡기·숨기기는 반드시 조합키여야 한다. 예전 설정에는 수정자 없이 저장된 값이 있는데,
        // 그대로 두면 맨 키·맨 클릭이 전역으로 걸려 평소 타이핑과 클릭까지 가로챈다.
        if (CatchHotkeyMod == 0 && (CatchHotkeyVk != 0 || CatchHotkeyMouse != 0))
        {
            Services.Logger.Info("Repaired bare catch hotkey -> added Ctrl.");
            CatchHotkeyMod = ModCtrl;
            repairedAny = true;
        }
        if (HideHotkeyMod == 0 && (HideHotkeyVk != 0 || HideHotkeyMouse != 0))
        {
            Services.Logger.Info("Repaired bare hide hotkey -> added Ctrl.");
            HideHotkeyMod = ModCtrl;
            repairedAny = true;
        }

        return repairedAny;
    }

    /// <summary>
    /// 종이비행기 테마로 바꾸기 직전의 던지기 가중치. 종이비행기는 가중치를 최소로 쓰기 때문에,
    /// 다른 테마로 돌아갈 때 이 값으로 되돌린다. 0 이하 = 되돌릴 값 없음(지금 종이비행기가 아니다).
    /// 앱을 종이비행기 상태로 끄고 다시 켜도 잃지 않도록 설정에 함께 저장한다.
    /// </summary>
    public double ThrowPowerBeforePaperPlane { get; set; }

    /// <summary>이 속도(px/s) 미만이면 완전히 정지시켜 저속 진동을 막는다.</summary>
    public double StopThreshold { get; set; } = 20.0;

    /// <summary>한 프레임 이동량이 이 값(px)을 넘으면 substep 으로 분할해 터널링 방지.</summary>
    public double SubstepMaxPx { get; set; } = 48.0;

    /// <summary>프레임 간 dt 상한(s). 창 멈춤/포커스 복귀 후 큰 점프 방지.</summary>
    public double MaxFrameDeltaSeconds { get; set; } = 0.05;

    // ── 스핀 ────────────────────────────────────────────────
    /// <summary>각속도 상한(deg/s).</summary>
    public double MaxAngularVelocity { get; set; } = 1200.0;

    /// <summary>스핀 감쇠율(1/s). 클수록 빨리 멈춘다. angVel *= exp(-SpinFriction*dt)</summary>
    public double SpinFriction { get; set; } = 0.55;

    /// <summary>이 각속도(deg/s) 미만이면 스핀 정지.</summary>
    public double SpinStopThreshold { get; set; } = 8.0;

    /// <summary>마그누스 계수. 옆으로 휘는 가속 = MagnusStrength * angVel(deg/s) * speed(px/s).</summary>
    public double MagnusStrength { get; set; } = 0.0005;

    /// <summary>벽 충돌 시 스핀이 접선 방향으로 튀게 하는 계수(px/s per deg/s).</summary>
    public double SpinWallKick { get; set; } = 0.5;

    /// <summary>벽 충돌 시 남는 스핀 비율(스핀이 벽에 소모됨).</summary>
    public double SpinWallRetain { get; set; } = 0.55;

    /// <summary>드래그 곡선 1도당 충전되는 스핀 배율(관성감).</summary>
    public double SpinChargeGain { get; set; } = 2.4;

    /// <summary>드래그 중 스핀의 완만한 감쇠(1/s). 직선 구간에서도 스핀이 유지되도록 작게.</summary>
    public double SpinChargeDecay { get; set; } = 0.7;

    /// <summary>스핀 이펙트(아크/반짝이)가 나타나기 시작하는 각속도(deg/s).</summary>
    public double SpinFxMinAngular { get; set; } = 140.0;

    // ── 표면 스핀(끌어치기/밀어치기: 세로 스핀) ─────────────
    /// <summary>표면 스핀(px/s)당 샷축 가속 배율(1/s). 클수록 되돌림/밀림이 강하다.
    /// 되돌아오는 총 속도변화 ≈ 표면스핀 × (DrawFollowStrength / SurfaceSpinFriction).</summary>
    [JsonIgnore] public double DrawFollowStrength { get; set; } = 1.1;

    /// <summary>표면 스핀 감쇠율(1/s). 작을수록 공이 더 멀리 나갔다가 천천히 돌아온다.</summary>
    [JsonIgnore] public double SurfaceSpinFriction { get; set; } = 1.0;

    /// <summary>이 표면스핀(px/s) 미만이면 정지 처리.</summary>
    [JsonIgnore] public double SurfaceSpinStopThreshold { get; set; } = 4.0;

    // ── 입력(던지기) ────────────────────────────────────────
    /// <summary>투척 속도 계산에 사용할 최근 샘플 시간창(ms). 짧을수록 놓는 순간의 실제 속도에 가깝다.</summary>
    [JsonIgnore] public double ThrowSampleWindowMs { get; set; } = 60.0;

    // ── 슬라임 크기/시각 ────────────────────────────────────
    /// <summary>슬라임 크기(물리 픽셀 기준 지름).</summary>
    private double _slimeSize = 96.0;
    public double SlimeSize { get => _slimeSize; set => Set(ref _slimeSize, value); }

    /// <summary>말랑함(Slime Softness). Squash/Stretch 강도 스케일. 0~1</summary>
    private double _softness = 0.5;
    public double Softness { get => _softness; set => Set(ref _softness, value); }

    /// <summary>이동 중 최대 늘어남 비율(Stretch 상한).</summary>
    public double MaxStretch { get; set; } = 0.35;

    /// <summary>스쿼시/스트레치가 원래 형태로 복귀하는 스프링 강성(클수록 빠르게 복귀).</summary>
    public double AnimationStiffness { get; set; } = 18.0;

    // ── 상호작용 모드 ───────────────────────────────────────
    /// <summary>던지기 활성화.</summary>
    private bool _throwMode = true;
    public bool ThrowMode { get => _throwMode; set => Set(ref _throwMode, value); }

    /// <summary>클릭 펀치 활성화(Phase 4 에서 시각/음향 확장).</summary>
    private bool _punchMode = true;
    public bool PunchMode { get => _punchMode; set => Set(ref _punchMode, value); }

    /// <summary>펀치(때리기) 시 튀는 초기 속도(px/s).</summary>
    public double PunchImpulse { get; set; } = 700.0;

    /// <summary>드래그로 판정할 최소 이동 거리(px). 이하이면 클릭(펀치)으로 본다.</summary>
    public double ClickMoveThreshold { get; set; } = 6.0;

    /// <summary>이 속도(px/s)보다 빠르게 움직이던 것을 클릭하면 "낚아채기"로 판정(펀치 X).</summary>
    public double CatchSpeedThreshold { get; set; } = 3.0;

    /// <summary>슬라임 가장자리에서 이 거리(px) 안을 클릭하면 낚아챈다(전역 훅). 빠른 슬라임도 근처 클릭으로 잡기.</summary>
    public double CatchExtraPx { get; set; } = 85.0;

    // ── 잡기 단축키(전역) ───────────────────────────────────
    /// <summary>Win32 수정자 비트(RegisterHotKey 규격).</summary>
    public const int ModAlt = 1, ModCtrl = 2, ModShift = 4, ModWin = 8;

    /// <summary>
    /// 잡기 단축키 수정자(Win32: ALT=1,CTRL=2,SHIFT=4,WIN=8 조합). 기본 <b>Ctrl</b>.
    /// 이 키를 누른 채로 눌러야 공이 잡힌다 — 맨 좌클릭으로 잡히면 공이 놓인 자리를
    /// 지나칠 때마다 의도치 않게 끌려다닌다.
    /// </summary>
    private int _catchHotkeyMod = ModCtrl;
    public int CatchHotkeyMod { get => _catchHotkeyMod; set => Set(ref _catchHotkeyMod, value); }

    /// <summary>잡기 단축키 가상키 코드(키보드). 0이면 키보드 트리거 없음. 기본 'G'(0x47).</summary>
    private int _catchHotkeyVk = 0x47;
    public int CatchHotkeyVk { get => _catchHotkeyVk; set => Set(ref _catchHotkeyVk, value); }

    /// <summary>잡기 단축키 마우스 버튼 트리거. 0=없음, 1=좌, 2=우, 3=중앙. (수정자와 조합). 기본 좌클릭.</summary>
    private int _catchHotkeyMouse = 1;
    public int CatchHotkeyMouse { get => _catchHotkeyMouse; set => Set(ref _catchHotkeyMouse, value); }

    /// <summary>
    /// 잡기 단축키를 새 기본값(Ctrl + 좌클릭)으로 한 번만 초기화했는가.
    /// 예전 설정 파일에는 수정자 없이 저장돼 있어, 그대로 두면 계속 맨 좌클릭으로 잡힌다.
    /// </summary>
    public bool CatchHotkeyResetToCtrl { get; set; }

    /// <summary>
    /// 예전 설정을 새 기본값으로 한 번 맞춘다(이미 했으면 아무것도 하지 않는다).
    /// 사용자가 그 뒤에 바꾼 값은 다시 건드리지 않는다.
    /// </summary>
    public void MigrateCatchHotkeyOnce()
    {
        if (CatchHotkeyResetToCtrl) return;
        CatchHotkeyResetToCtrl = true;

        CatchHotkeyMod = ModCtrl;   // Ctrl
        CatchHotkeyMouse = 1;       // 좌클릭
    }

    /// <summary>
    /// 농구공 조준 단축키 가상키(누른 상태로 공을 뒤로 끌면 포물선 유도선 → 떼면 발사).
    /// 기본 Shift(0x10). 0이면 조준 기능 사용 안 함.
    /// </summary>
    private int _basketballAimVk = 0x10;
    public int BasketballAimVk { get => _basketballAimVk; set => Set(ref _basketballAimVk, value); }
    // ── 빠르게 숨기기 단축키(전역) ──────────────────────────
    /// <summary>숨기기 단축키 수정자(Win32: ALT=1,CTRL=2,SHIFT=4,WIN=8 조합). 기본 ALT.</summary>
    private int _hideHotkeyMod = 1;
    public int HideHotkeyMod { get => _hideHotkeyMod; set => Set(ref _hideHotkeyMod, value); }

    /// <summary>숨기기 단축키 가상키 코드. 0이면 키보드 트리거 없음. 기본 ` (VK_OEM_3 = 0xC0).</summary>
    private int _hideHotkeyVk = 0xC0;
    public int HideHotkeyVk { get => _hideHotkeyVk; set => Set(ref _hideHotkeyVk, value); }

    /// <summary>숨기기 단축키 마우스 버튼 트리거. 0=없음, 1=좌, 2=우, 3=중앙. (수정자와 조합)</summary>
    private int _hideHotkeyMouse;
    public int HideHotkeyMouse { get => _hideHotkeyMouse; set => Set(ref _hideHotkeyMouse, value); }

    /// <summary>
    /// 종이비행기 바람 단축키의 키 1개(가상키). 수정자는 <c>Ctrl</c> 고정이라 저장하지 않는다.
    /// 기본 Space(0x20) → 표시는 "Ctrl + Space". 0이면 바람 단축키 사용 안 함.
    /// 활공 중에만 전역 등록되므로, 평소에는 다른 앱이 이 조합을 그대로 쓴다.
    /// </summary>
    private int _windHotkeyVk = 0x20;
    public int WindHotkeyVk { get => _windHotkeyVk; set => Set(ref _windHotkeyVk, value); }

    /// <summary>효과음 사용(Phase 4).</summary>
    private bool _soundEnabled = true;
    public bool SoundEnabled { get => _soundEnabled; set => Set(ref _soundEnabled, value); }

    /// <summary>효과음 기본 볼륨(0~1).</summary>
    private double _soundVolume = 0.7;
    public double SoundVolume { get => _soundVolume; set => Set(ref _soundVolume, value); }

    // ── 파티클 / 충돌 효과(Phase 4) ─────────────────────────
    /// <summary>충돌 파티클 사용.</summary>
    private bool _particlesEnabled = true;
    public bool ParticlesEnabled { get => _particlesEnabled; set => Set(ref _particlesEnabled, value); }

    /// <summary>약한 충돌 시 파티클 최소 개수.</summary>
    public int ParticleBaseCount { get; set; } = 5;

    /// <summary>강한 충돌 시 파티클 최대 개수.</summary>
    public int ParticleMaxCount { get; set; } = 22;

    /// <summary>파티클 수명(초).</summary>
    public double ParticleLifeSeconds { get; set; } = 0.7;

    /// <summary>파티클 중력 가속도(px/s^2).</summary>
    public double ParticleGravity { get; set; } = 2600.0;

    /// <summary>파티클 방출 속도 최소/최대(px/s).</summary>
    public double ParticleSpeedMin { get; set; } = 120.0;
    public double ParticleSpeedMax { get; set; } = 640.0;

    /// <summary>파티클 크기(px).</summary>
    public double ParticleSize { get; set; } = 9.0;

    /// <summary>이 비율(MaxSpeed 대비) 미만이면 효과(소리/파티클) 생략.</summary>
    public double ImpactSoftFraction { get; set; } = 0.12;

    /// <summary>BOING → SPLAT 전환 비율.</summary>
    public double ImpactMediumFraction { get; set; } = 0.30;

    /// <summary>SPLAT → BONK 전환 비율.</summary>
    public double ImpactHardFraction { get; set; } = 0.60;

    /// <summary>항상 위(Topmost).</summary>
    private bool _alwaysOnTop = true;
    public bool AlwaysOnTop { get => _alwaysOnTop; set => Set(ref _alwaysOnTop, value); }

    /// <summary>물리 일시 정지.</summary>
    private bool _paused = false;
    public bool Paused { get => _paused; set => Set(ref _paused, value); }

    /// <summary>슬라임 표시 여부.</summary>
    private bool _slimeVisible = true;
    public bool SlimeVisible { get => _slimeVisible; set => Set(ref _slimeVisible, value); }

    // (제거됨) ShowReleaseNotes / AutoRestartOnUpdate
    //   업데이트는 이제 묻지 않는다 — 켤 때 새 버전이 있으면 진행바만 띄우고 적용 후 자동 재시작.
    //   변경 내용은 설정 → 일반 → "최근 변경 내용 보기" 로 언제든 확인한다.

    /// <summary>
    /// 마지막으로 실행된 버전. 지금 버전과 다르면 방금 업데이트된 것으로 보고
    /// 탐색기 아이콘 캐시를 갱신한다(바로가기가 옛 아이콘으로 남는 문제).
    /// </summary>
    public string LastRunVersion { get; set; } = "";

    /// <summary>작업표시줄에 ThrowMe 를 표시할지(Alt+Tab 에도 함께 나온다).</summary>
    private bool _showInTaskbar = true;
    public bool ShowInTaskbar { get => _showInTaskbar; set => Set(ref _showInTaskbar, value); }

    /// <summary>
    /// 공 주변에서 클릭을 받아들이는 여유(px). 공 반지름 + 이 값이 클릭 반경이 된다.
    ///
    /// 예전에는 창 전체(공 크기의 4배)가 클릭을 받아서, 공이 날아가는 동안 보이지 않는
    /// 사각형이 다른 창의 클릭을 삼켰다(설정창 탭이 안 눌리는 문제). 공 근처로 좁힌다.
    /// </summary>
    private double _clickMarginPx = 28.0;
    public double ClickMarginPx { get => _clickMarginPx; set => Set(ref _clickMarginPx, value); }

    /// <summary>공이 다른 PC로 넘어가는 등 알림을 우측 하단 토스트로 보여줄지.</summary>
    private bool _showToasts = true;
    public bool ShowToasts { get => _showToasts; set => Set(ref _showToasts, value); }

    /// <summary>사용법 힌트를 중간중간 토스트로 띄워줄지(약 3분 간격 순환).</summary>
    private bool _showUsageTips = true;
    public bool ShowUsageTips { get => _showUsageTips; set => Set(ref _showUsageTips, value); }

    /// <summary>날아가는 중 클릭으로 방향을 바꿀 때, 이 속도(px/s) 이상이면 "되치기"로 본다.</summary>
    public double DeflectMinSpeed { get; set; } = 150.0;

    /// <summary>표시할 스킨(젤리/당구공 등).</summary>
    private SlimeSkinKind _skin = SlimeSkinKind.Jelly;
    public SlimeSkinKind Skin { get => _skin; set => Set(ref _skin, value); }

    /// <summary>큐대로 때리기 모드(당구공 전용). 켜면 공 근처 클릭→큐대 당겨 밀어서 발사.</summary>
    private bool _cueStickMode;
    public bool CueStickMode { get => _cueStickMode; set => Set(ref _cueStickMode, value); }

    /// <summary>큐대 당긴 거리(px)당 발사 속도 배율.</summary>
    [JsonIgnore] public double CuePowerScale { get; set; } = 11.0;

    // ── 테마별 커스텀 이미지(덧씌우기) ──────────────────────
    /// <summary>커스텀 이미지 덧씌우기 사용. 끄면 이미지를 보관한 채 원래 스킨만 보인다.</summary>
    private bool _skinImageEnabled = true;
    public bool SkinImageEnabled { get => _skinImageEnabled; set => Set(ref _skinImageEnabled, value); }

    /// <summary>공 지름 대비 커스텀 이미지 크기(1.0 = 공 지름에 꽉 맞춤). 1.0 초과는 원 밖이 잘린다.</summary>
    private double _skinImageScale = 1.0;
    public double SkinImageScale { get => _skinImageScale; set => Set(ref _skinImageScale, value); }

    /// <summary>테마별 커스텀 이미지 원본 파일명(표시용). 키 = <see cref="SlimeSkinKind"/> 이름.
    /// 실제 이미지는 %APPDATA%/ThrowMe/skins/&lt;스킨&gt;.png 로 복사해 보관한다
    /// (원본을 옮기거나 지워도 깨지지 않도록). 키가 있으면 그 테마에 커스텀 이미지가 있다는 뜻.
    /// Dictionary 는 내용 변경이 자동 통보되지 않으므로 <see cref="NotifySkinImagesChanged"/> 를 호출한다.</summary>
    public Dictionary<string, string> SkinImages { get; set; } = new();

    /// <summary>SkinImages 내용을 바꾼 뒤 호출 — 화면 갱신·자동 저장을 유발한다.</summary>
    public void NotifySkinImagesChanged()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SkinImages)));
}
