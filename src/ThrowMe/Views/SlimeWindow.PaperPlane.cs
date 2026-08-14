using System.Windows;
using ThrowMe.Effects;
using ThrowMe.Models;
using ThrowMe.Physics;
using ThrowMe.Views.Skins;

namespace ThrowMe.Views;

/// <summary>
/// 종이비행기 테마 전용 동작(themes/paperplane.md §1.1 확정 규칙).
///  ① 활공: 속도에 비례한 양력으로 미끄러지듯 나아간다
///  ② 중력: 농구공과 같은 방식이지만 종이답게 더 작다
///  ③ 벽 충돌: 튕기지 않고 구겨지며 떨어진다 → 바닥에서 원상복구
///  ④ 좌우 흔들림(sway)
///  ⑤ 마우스 바람: 커서를 대면 커서 방향대로 밀려난다 + 바람 이펙트
///  ⑥ 바람 단축키(Ctrl 고정 + 사용자 키): 아래로 바람을 쏴서 더 오래 활공.
///     활공 중에만 전역 등록한다(평소 다른 앱의 Ctrl 조합을 가로채지 않게).
/// </summary>
public partial class SlimeWindow
{
    // ── 상수(느낌 조절) ─────────────────────────────────────
    /// <summary>종이비행기 중력(px/s^2). 농구공(2200)보다 훨씬 작아 천천히 가라앉는다.</summary>
    private const double PaperGravity = 520.0;

    /// <summary>공기저항(1/s). 기본값 0.9. 활공 길이를 정하는 값이다 —
    /// 속도가 <see cref="PaperMaxLift"/>/<see cref="PaperLiftPerSpeed"/>(=315px/s) 아래로 떨어지면
    /// 양력이 중력을 못 이겨 가라앉기 시작한다. 상한 속도(1800)에서 여기까지 약 2초, 착지까지 2~3초.</summary>
    private const double PaperFriction = 0.85;

    /// <summary>양력 계수: 속도(px/s) 1당 이만큼 위로 가속한다.</summary>
    private const double PaperLiftPerSpeed = 1.6;

    /// <summary>양력 상한(px/s^2). 중력에 거의 맞먹어 한동안 수평으로 미끄러진다.</summary>
    private const double PaperMaxLift = PaperGravity * 0.97;

    /// <summary>양력이 살아나는 최소 속도(px/s). 이보다 느려지면 스르륵 떨어진다.</summary>
    private const double PaperLiftMinSpeed = 30.0;

    /// <summary>좌우 흔들림 세기(px/s^2)와 주기(rad/s).</summary>
    private const double PaperSwayAccel = 620.0;
    private const double PaperSwayRate = 5.2;

    /// <summary>마우스 바람이 닿는 거리(공 크기 배수)와 세기(px/s^2).</summary>
    private const double PaperWindRadiusScale = 1.7;
    private const double PaperWindAccel = 2600.0;

    /// <summary>단축키 바람 한 번의 상승 충격(px/s)과 연사 간격(초).</summary>
    private const double PaperWindKick = 430.0;
    private const double PaperWindKickInterval = 0.12;

    /// <summary>바람 이펙트 방출 간격(초). 매 프레임 뿌리면 과하다.</summary>
    private const double PaperWindFxInterval = 0.07;

    /// <summary>구겨진 뒤 바닥에서 원상복구까지 기다리는 시간(초).</summary>
    private const double PaperUncrumpleDelay = 0.45;

    /// <summary>종이비행기에서 쓰는 던지기 가중치(설정 슬라이더 최솟값과 같다).
    /// 종이는 세게 던져 봐야 소용없고, 살살 띄워 활공시키는 편이 어울린다.</summary>
    private const double PaperThrowPower = 0.3;

    /// <summary>던질 때의 속도 상한(px/s). 공을 던지는 것과 성격이 달라, 아무리 빠르게 휘둘러도
    /// 이보다 세게 날아가지 않는다(손을 떠나면 바람에 실려 가는 느낌).</summary>
    private const double PaperMaxThrow = 1800.0;

    /// <summary>던지기 배율. 가중치를 최소(0.3)로 쓰기 때문에 그대로면 너무 힘없이 나간다.
    /// 실효 배율은 0.3 × 2.5 ≈ 0.75 — 슬라이더를 올리면 여전히 그만큼 더 세게 나간다.</summary>
    private const double PaperThrowScale = 2.5;

    /// <summary>종이비행기 던지기: 배율을 곱한 뒤 상한으로 눌러 준다.</summary>
    private Vector2 ClampPaperThrow(Vector2 throwVelocity)
        => (throwVelocity * PaperThrowScale).ClampLength(PaperMaxThrow);

    // ── 상태 ────────────────────────────────────────────────
    private double _paperHeadingDeg;      // 화면에 보여 주는 자세(부드럽게 따라간다)
    private bool _paperHeadingReady;      // 첫 방향을 잡았는가
    private double _paperSwayPhase;        // 흔들림 위상
    private double _paperLastWindFx;       // 마지막 바람 이펙트 시각
    private double _paperLastWindKick;     // 마지막 단축키 바람 시각
    private double _paperGroundedSince;    // 구겨진 채 바닥에 닿은 시각(0=아직)
    private bool _windHotkeyRegistered;
    private bool _paperPhysicsApplied;     // 종이비행기용 반발·마찰 오버라이드를 걸어 둔 상태인가

    private const int WindHotkeyId = 0xB003;
    private const uint MOD_CONTROL = 0x0002;  // 바람 단축키의 수정자는 Ctrl 고정

    /// <summary>지금 테마가 종이비행기인가.</summary>
    private bool PaperPlaneOn => _settings.Skin == SlimeSkinKind.PaperPlane;

    /// <summary>공중에 떠서 활공 중인가(바닥에 닿아 있지 않고, 잡고 있지도 않다).</summary>
    private bool PaperGliding => PaperPlaneOn && !_isDragging && !_settings.Paused
                                 && _settings.SlimeVisible && !_physics.IsGrounded();

    private PaperPlaneSkin? PaperSkin => SkinHost.Content as PaperPlaneSkin;

    /// <summary>스킨 적용 시 호출: 종이비행기용 물리값·표시를 맞춘다(다른 테마면 되돌린다).</summary>
    private void ApplyPaperPlaneBehavior()
    {
        bool on = PaperPlaneOn;

        // 종이비행기는 굴러가는 스핀을 쓰지 않는다(방향은 좌우 뒤집기 + 기수 각도로 표현).
        if (on)
        {
            _physics.AngularVelocity = 0;
            _physics.SurfaceSpin = 0;
            _physics.SpinAngle = 0;
            _dragSpin = 0;
        }

        // 벽에 튕기지 않고 구겨지며 떨어진다 → 반발을 없애고, 공기저항도 종이 값으로 낮춘다.
        // 농구 골대도 같은 오버라이드를 쓰므로, 우리가 걸어 둔 경우에만 되돌린다(골대 값을 덮지 않게).
        if (on)
        {
            _physics.RestitutionOverride = 0.0;
            _physics.FrictionOverride = PaperFriction;
            _paperPhysicsApplied = true;
        }
        else if (_paperPhysicsApplied)
        {
            _physics.RestitutionOverride = null;
            _physics.FrictionOverride = null;
            _paperPhysicsApplied = false;
        }

        // 공통 원형 그림자는 종이비행기에 어울리지 않는다(스킨이 자기 그림자를 갖고 있다).
        CommonShadow.Visibility = on ? Visibility.Collapsed : Visibility.Visible;

        ApplyPaperPlaneThrowPower();

        _paperHeadingReady = false;
        _paperGroundedSince = 0;
        if (!on) UnregisterWindHotkey();
        else EnsureRendering(); // 중력으로 곧 가라앉으므로 루프를 깨운다
    }

    /// <summary>
    /// 종이비행기로 들어오면 던지기 가중치를 최소로 낮추고, 원래 값은 설정에 보관한다.
    /// 다른 테마로 나가면 보관한 값으로 그대로 되돌린다.
    /// 종이비행기를 쓰는 동안 사용자가 직접 가중치를 조절한 경우는 그 값을 존중한다(다시 낮추지 않는다).
    /// </summary>
    private void ApplyPaperPlaneThrowPower()
    {
        if (PaperPlaneOn)
        {
            if (_settings.ThrowPowerBeforePaperPlane > 0) return; // 이미 종이비행기로 전환해 둔 상태
            _settings.ThrowPowerBeforePaperPlane = _settings.ThrowPower;
            _settings.ThrowPower = PaperThrowPower;
        }
        else if (_settings.ThrowPowerBeforePaperPlane > 0)
        {
            // 보관값을 먼저 비운 뒤 되돌린다 — 설정 저장이 ThrowPower 변경 알림을 타므로,
            // 순서가 뒤바뀌면 "되돌릴 값이 남아 있는" 상태가 파일에 저장된다.
            double restore = _settings.ThrowPowerBeforePaperPlane;
            _settings.ThrowPowerBeforePaperPlane = 0;
            _settings.ThrowPower = restore;
        }
    }

    /// <summary>물리 적분 직전: 양력·흔들림·바람을 속도에 더한다.</summary>
    private void TickPaperPlaneAero(double dt)
    {
        UpdateWindHotkey();
        if (!PaperGliding) return;

        bool crumpled = PaperSkin?.IsCrumpled == true;
        Vector2 v = _physics.Velocity;
        double speed = v.Length;

        // ① 양력 — 구겨진 종이 뭉치는 활공하지 않는다.
        if (!crumpled && speed > PaperLiftMinSpeed)
        {
            Vector2 up = new Vector2(v.Y, -v.X) / speed; // 진행 방향에 수직
            if (up.Y > 0) up = up * -1.0;                 // 항상 위쪽 성분으로
            double lift = Math.Min(speed * PaperLiftPerSpeed, PaperMaxLift);
            _physics.Velocity += up * (lift * dt);

            // ④ 좌우 흔들림 — 수직 방향으로 살랑살랑(속도가 빠를수록 크게)
            _paperSwayPhase += dt * PaperSwayRate;
            double swayGain = Math.Clamp(speed / 700.0, 0, 1);
            _physics.Velocity += up * (Math.Sin(_paperSwayPhase) * PaperSwayAccel * swayGain * dt);
        }

        // ⑤ 마우스 바람 — 커서를 가져다 대면 커서 반대 방향으로 밀려난다.
        if (!crumpled) ApplyMouseWind(dt);
    }

    /// <summary>커서가 근처에 있으면 커서에서 멀어지는 방향으로 밀고 바람 이펙트를 뿌린다.</summary>
    private void ApplyMouseWind(double dt)
    {
        double half = _settings.SlimeSize / 2.0;
        Vector2 center = _physics.Position + new Vector2(half, half);
        Vector2 cursor = CursorPhysical();
        Vector2 away = center - cursor;
        double dist = away.Length;
        double radius = _settings.SlimeSize * PaperWindRadiusScale;
        if (dist >= radius || dist < 1e-3) return;

        double strength = 1.0 - dist / radius;          // 가까울수록 강하게
        Vector2 dir = away / dist;
        _physics.Velocity += dir * (PaperWindAccel * strength * dt);

        EmitWindFx(cursor + dir * (dist * 0.35), dir, strength);
    }

    /// <summary>물리 적분 뒤: 자세(좌우 뒤집기 + 기수 각도)와 구겨짐 복구를 갱신한다.</summary>
    private void TickPaperPlaneVisual(double dt)
    {
        if (PaperSkin is not PaperPlaneSkin skin) return;

        // ③ 구겨진 상태면 바닥에서 잠깐 뒤 원래 모양으로 돌아온다.
        if (skin.IsCrumpled)
        {
            if (_physics.IsGrounded() && _physics.Velocity.Length < 40.0)
            {
                if (_paperGroundedSince <= 0) _paperGroundedSince = Now;
                else if (Now - _paperGroundedSince >= PaperUncrumpleDelay)
                {
                    skin.SetCrumpled(false);
                    _paperGroundedSince = 0;
                    _paperHeadingReady = false;
                }
            }
            else _paperGroundedSince = 0;
            return;
        }

        Vector2 v = _physics.Velocity;
        double speed = v.Length;
        if (speed < 45.0)
        {
            skin.SetRestPose();
            return;
        }

        double target = Math.Atan2(v.Y, v.X) * 180.0 / Math.PI;
        if (!_paperHeadingReady)
        {
            _paperHeadingDeg = target;
            _paperHeadingReady = true;
        }
        else
        {
            // 급격히 꺾이지 않게 지수 보간(각도는 -180~180 으로 정규화해 최단 경로로).
            double a = 1.0 - Math.Exp(-9.0 * dt);
            _paperHeadingDeg += NormalizeAngle(target - _paperHeadingDeg) * a;
        }
        skin.SetHeading(_paperHeadingDeg);
    }

    /// <summary>구겨진 종이비행기를 즉시 펴 준다(집어 들거나 커서로 회수했을 때).</summary>
    private void UncrumplePaperPlane()
    {
        if (PaperSkin is not PaperPlaneSkin skin || !skin.IsCrumpled) return;
        skin.SetCrumpled(false);
        _paperGroundedSince = 0;
        _paperHeadingReady = false;
    }

    /// <summary>벽에 부딪혔을 때: 공중이었다면 구겨진다(튕기지 않는다).</summary>
    private void OnPaperPlaneCollided(double impactSpeed)
    {
        if (PaperSkin is not PaperPlaneSkin skin || skin.IsCrumpled) return;
        if (impactSpeed < 120.0) return;      // 살짝 스친 정도로는 구겨지지 않는다
        skin.SetCrumpled(true);
        _paperGroundedSince = 0;
    }

    // ── 바람 단축키(Ctrl 고정 + 사용자 키 1개) ────────────────
    /// <summary>활공 중에만 전역 등록하고, 그 밖의 상태에서는 즉시 해제한다.
    /// 설정창에서 키를 다시 지정하는 동안에는 잡지 않는다(입력이 설정창에 들어가야 한다).</summary>
    private void UpdateWindHotkey()
    {
        bool want = PaperGliding && !SettingsOpen && _settings.WindHotkeyVk != 0;
        if (want == _windHotkeyRegistered) return;
        if (want) RegisterWindHotkey();
        else UnregisterWindHotkey();
    }

    private void RegisterWindHotkey()
    {
        if (_hwnd == IntPtr.Zero || _windHotkeyRegistered) return;
        if (RegisterHotKey(_hwnd, WindHotkeyId, MOD_CONTROL, (uint)_settings.WindHotkeyVk))
            _windHotkeyRegistered = true;
    }

    private void UnregisterWindHotkey()
    {
        if (!_windHotkeyRegistered) return;
        UnregisterHotKey(_hwnd, WindHotkeyId);
        _windHotkeyRegistered = false;
    }

    /// <summary>⑥ 아래에서 바람을 쏴 위로 밀어 올린다(체공 연장). 연사는 간격으로 제한.</summary>
    private void BlowWindUnderPlane()
    {
        if (!PaperGliding) return;
        if (PaperSkin?.IsCrumpled != false) return;   // 구겨진 종이는 바람으로 못 띄운다
        if (Now - _paperLastWindKick < PaperWindKickInterval) return;
        _paperLastWindKick = Now;

        _physics.Velocity = _physics.Velocity.WithY(_physics.Velocity.Y - PaperWindKick);

        double half = _settings.SlimeSize / 2.0;
        Vector2 under = _physics.Position + new Vector2(half, _settings.SlimeSize * 0.9);
        EmitWindFx(under, new Vector2(0, -1), 1.0);
        EnsureRendering();
    }

    /// <summary>바람 이펙트(파티클 오버레이가 그린다 — 숨기기 대상에 이미 포함).</summary>
    private void EmitWindFx(Vector2 origin, Vector2 dir, double strength)
    {
        if (Now - _paperLastWindFx < PaperWindFxInterval) return;
        _paperLastWindFx = Now;
        _particles.EmitWind(origin, dir, strength);
    }

    private static double NormalizeAngle(double deg)
    {
        while (deg > 180) deg -= 360;
        while (deg < -180) deg += 360;
        return deg;
    }
}
