using System.Windows;
using ThrowMe.Models;

namespace ThrowMe.Physics;

/// <summary>
/// 한 번의 Update 결과. 애니메이션·효과음 트리거에 사용한다.
/// </summary>
public readonly struct PhysicsStepResult
{
    /// <summary>이번 프레임에 벽에 부딪혔는가.</summary>
    public bool Collided { get; init; }

    /// <summary>충돌 시 반전 직전의 속도 크기(px/s). 충돌 세기 판정용.</summary>
    public double MaxImpactSpeed { get; init; }

    /// <summary>속도가 0 이 되어 정지(수면) 상태인가. 렌더 루프 유휴화 판단용.</summary>
    public bool Sleeping { get; init; }

    /// <summary>최대 세기 충돌의 벽 법선(플레이 영역 안쪽 방향). 방향성 이펙트용.</summary>
    public Vector2 CollisionNormal { get; init; }

    /// <summary>최대 세기 충돌이 일어난 순간의 슬라임 top-left 위치(물리 픽셀).
    /// 프레임 종료 위치와 다르다 — 빠를수록 한 프레임에 멀리 가므로,
    /// 이펙트(파티클·문구·스파크)는 반드시 이 값을 써야 벽에 붙어 나온다.</summary>
    public Vector2 CollisionPosition { get; init; }
}

/// <summary>
/// 위치·속도·마찰·충돌·반사를 계산하는 순수 물리 엔진(UI 비의존).
/// 좌표는 물리 스크린 픽셀(top-left 기준).
///
/// 충돌 판정은 <see cref="IWalkableArea"/> 에 위임한다.
/// - 다음 위치가 다른 모니터로 연결되면 통과(IsRectValid == true)
/// - 어떤 모니터에도 포함되지 않으면 벽(반사)
/// - X/Y 축을 분리해 각 축의 충돌 방향을 판정한다.
/// </summary>
public sealed class SlimePhysicsEngine
{
    private readonly AppSettings _settings;

    public IWalkableArea Area { get; set; }

    /// <summary>슬라임 top-left 위치(물리 픽셀).</summary>
    public Vector2 Position { get; set; }

    /// <summary>속도(px/s).</summary>
    public Vector2 Velocity { get; set; }

    /// <summary>각속도(deg/s). 양수=시계방향(화면 기준). 옆으로 휘는 사이드 스핀(마그누스)에 사용.</summary>
    public double AngularVelocity { get; set; }

    /// <summary>표면 스핀(px/s). 샷 축 방향 가감속용. 양수=밀어치기(전진), 음수=끌어치기(되돌아옴).</summary>
    public double SurfaceSpin { get; set; }

    /// <summary>표면 스핀이 작용하는 샷 축(단위 벡터). 발사 시점의 진행 방향.</summary>
    public Vector2 SpinShotDir { get; set; }

    /// <summary>누적 회전각(deg). 시각 회전에 사용.</summary>
    public double SpinAngle { get; set; }

    /// <summary>중력 가속도(px/s^2, 아래 +y). 0이면 무중력(기본). 농구공에서만 설정한다.</summary>
    public double GravityY { get; set; } = 0.0;

    /// <summary>반발 계수 오버라이드(설정 대신 사용). null이면 설정값. 골대 ON 시 현실값 고정용.</summary>
    public double? RestitutionOverride { get; set; }
    /// <summary>마찰(공기저항) 오버라이드(설정 대신 사용). null이면 설정값. 골대 ON 시 현실값 고정용.</summary>
    public double? FrictionOverride { get; set; }

    /// <summary>벽에 튕길 때 반사 방향을 좌우로 흔드는 최대 각도(deg). 0 이면 정직한 입사각=반사각.
    /// 젤리 테마에서만 켠다(SlimeWindow.UpdateSkinBehavior 에서 주입).</summary>
    public double RandomBounceSpreadDeg { get; set; } = 0.0;

    /// <summary>
    /// 자동 이동용 추진 가속도(px/s^2). 0 이 아니면 슬라임이 스스로 그 방향으로 밀린다.
    ///
    /// 속도를 직접 넣지 않고 가속도로 주는 이유는, 마찰이 있으면 속도가 저절로
    /// <c>추진력 ÷ 마찰</c> 에서 멎기 때문이다 — 목표 속도를 그 식으로 역산해 넣으면
    /// 프레임 간격과 무관하게 일정한 속도로 기어간다.
    ///
    /// 추진 중에는 정지 임계로 속도를 죽이지 않는다. 임계가 20px/s 라
    /// 진짜 '꼬물꼬물' 속도는 그대로 0 이 되어 버리기 때문이다.
    /// </summary>
    public Vector2 Propulsion { get; set; } = Vector2.Zero;

    /// <summary>
    /// 자동 이동 중인가. 켜져 있으면 정지 임계로 속도를 죽이지도, 수면에 들지도 않는다.
    /// <see cref="Propulsion"/> 이 0 인지로 판단하지 않는 이유는, 속도 제어기가 목표 속도에
    /// 도달하면 추진력이 순간적으로 0 이 되기 때문이다 — 그때 멈춰 버리면 딸꾹질처럼 끊긴다.
    /// </summary>
    public bool AutoMoving { get; set; }

    private bool Propelled => Propulsion.LengthSquared > 1e-9;

    private double EffRestitution => RestitutionOverride ?? _settings.Restitution;
    // 사용자의 감속 배율은 여기 한 곳에서 곱한다 — 테마·골대·볼링의 마찰 오버라이드에도 함께 걸린다.
    private double EffFriction => (FrictionOverride ?? _settings.Friction) * _settings.SlowdownScale;

    /// <summary>현재 적용 중인 마찰(1/s). 조준 유도선이 실제 궤적과 같게 그리도록 노출.</summary>
    public double EffectiveFriction => EffFriction;

    /// <summary>바닥에 붙어 이 속도(px/s) 미만으로 튀면 수직 속도를 죽여 무한 미세 바운스를 막는다.</summary>
    private const double LandStopSpeed = 180.0;

    /// <summary>바닥에 붙어 구를 때 수평 감쇠(1/s). 공이 자연스럽게 멈추도록(구름 마찰).</summary>
    private const double GroundFriction = 2.6;

    /// <summary>랜덤 반사 후에도 벽면과 최소한 이만큼(deg)은 벌어지게 한다.
    /// 벽과 거의 나란한 방향이 나오면 다음 substep 에서 곧바로 또 부딪혀 벽을 따라 떨기 때문.</summary>
    private const double MinExitAngleDeg = 10.0;

    /// <summary>이 각(deg) 안쪽으로 정면에 가깝게 맞으면 "벽을 따라 가던 쪽"이 없다고 보고
    /// 좌우를 무작위로 정한다. 그러지 않으면 정면 충돌이 늘 한쪽으로만 꺾인다.</summary>
    private const double HeadOnDeg = 2.0;

    private readonly Random _rng = new();

    public SlimePhysicsEngine(AppSettings settings, IWalkableArea area)
    {
        _settings = settings;
        Area = area;
    }

    public bool IsAtRest => Velocity.LengthSquared < 1e-6;

    private Rect RectFor(double x, double y) =>
        new(x, y, _settings.SlimeSize, _settings.SlimeSize);

    public bool IsCurrentPositionValid() => Area.IsRectValid(RectFor(Position.X, Position.Y));

    /// <summary>바닥(또는 아래 벽)에 닿아 더 내려갈 수 없는가. 중력 안착 판정용.</summary>
    public bool IsGrounded() => !Area.IsRectValid(RectFor(Position.X, Position.Y + 2.0));

    /// <summary>deltaTime(초) 기반으로 한 프레임 진행.</summary>
    public PhysicsStepResult Update(double dt)
    {
        bool gravity = GravityY != 0.0;

        // 무효한 자리에서 시작하면 어느 축으로도 유효한 이동이 없어 그 자리에 박힌다.
        // 들어온 경로(드래그·모니터 구성 변경·DPI 변경·핸드오프)와 무관하게 여기서 되돌린다.
        // 멀티 PC 에서 연결된 엣지를 넘어가는 중이면 그 좌표는 애초에 유효로 판정되므로 걸리지 않는다.
        if (!Area.IsRectValid(RectFor(Position.X, Position.Y))
            && TryFindValidNear(Position, out Vector2 rescued))
            Position = rescued;

        // 이동도 회전도 표면스핀도 없을 때만 완전 정지로 간주.
        // 중력이 있으면 "바닥에 닿아 있을 때"만 정지(공중에 멈춘 공은 낙하해야 함).
        if (dt <= 0 || (IsAtRest
                        && !AutoMoving         // 자동 이동 중이면 아직 밀 힘이 남아 있다
                        && Math.Abs(AngularVelocity) < _settings.SpinStopThreshold
                        && Math.Abs(SurfaceSpin) < _settings.SurfaceSpinStopThreshold
                        && (!gravity || IsGrounded())))
        {
            AngularVelocity = 0;
            SurfaceSpin = 0;
            return new PhysicsStepResult { Sleeping = true };
        }

        // 0) 중력 가속(농구공)
        if (gravity)
            Velocity = Velocity.WithY(Velocity.Y + GravityY * dt);

        // 0.5) 자동 이동 추진. 마찰보다 먼저 더해야 이번 프레임부터 마찰과 균형을 이룬다.
        if (Propelled)
            Velocity += Propulsion * dt;

        // 1) 마찰(프레임 독립 지수 감쇠)
        Velocity *= Math.Exp(-EffFriction * dt);

        // 1.5) 표면 스핀(끌어치기/밀어치기): 샷 축으로 가감속.
        //   음수(끌어치기)면 진행 반대로 힘 → 전진하다 감속·반전해 되돌아온다.
        //   양수(밀어치기)면 진행 방향으로 힘 → 더 끝까지 밀고 나간다.
        if (Math.Abs(SurfaceSpin) > 1e-3)
        {
            Velocity += SpinShotDir * (SurfaceSpin * _settings.DrawFollowStrength * dt);
            SurfaceSpin *= Math.Exp(-_settings.SurfaceSpinFriction * dt);
            if (Math.Abs(SurfaceSpin) < _settings.SurfaceSpinStopThreshold) SurfaceSpin = 0;
        }

        // 2) 최대 속도 제한
        Velocity = Velocity.ClampLength(_settings.EffectiveMaxSpeed);

        // 3) 터널링 방지: 이동량이 크면 substep 분할
        double travel = Velocity.Length * dt;
        int steps = Math.Max(1, (int)Math.Ceiling(travel / Math.Max(1.0, _settings.SubstepMaxPx)));
        double subDt = dt / steps;

        bool collided = false;
        double maxImpact = 0;
        Vector2 normal = Vector2.Zero;
        Vector2 hitPos = Position;   // 최대 세기 충돌 순간의 위치(프레임 끝 위치와 다름)

        for (int i = 0; i < steps; i++)
        {
            // 랜덤 반사는 substep 당 한 번만. 코너에서 X·Y 가 같이 부딪힐 때
            // 두 번 겹쳐 돌면 흔들기 폭이 합쳐져 방향이 완전히 통제를 벗어난다.
            bool randomized = false;

            // ── X 축 ────────────────────────────────
            double dx = Velocity.X * subDt;
            if (dx != 0)
            {
                double tryX = Position.X + dx;
                if (Area.IsRectValid(RectFor(tryX, Position.Y)))
                {
                    Position = Position.WithX(tryX);
                }
                else
                {
                    // 벽 접촉 지점까지 이분 근접 후 반사
                    double contactX = ResolveContact(dx, isXAxis: true);
                    Position = Position.WithX(contactX);
                    double impactX = Math.Abs(Velocity.X);
                    if (impactX > maxImpact)
                    {
                        maxImpact = impactX;
                        normal = new Vector2(-Math.Sign(dx), 0); // 진행 반대 = 안쪽
                        hitPos = Position;                        // 벽에 닿은 그 지점
                    }
                    Velocity = Velocity.WithX(-Velocity.X * EffRestitution);
                    // 추진 방향도 같이 꺾는다. 안 그러면 자동 이동이 벽에 코를 박고 계속 민다.
                    if (Propelled) Propulsion = Propulsion.WithX(-Propulsion.X);
                    // 스핀이 벽을 물어 접선(세로) 방향으로 튀고, 스핀은 소모된다.
                    if (Math.Abs(AngularVelocity) > 1e-3)
                    {
                        if (!_settings.InfiniteBounce) // 무한 튕기기는 힘을 얻지도 않는다(아래 설명)
                            Velocity = Velocity.WithY(Velocity.Y + AngularVelocity * _settings.SpinWallKick);
                        AngularVelocity *= _settings.SpinWallRetain;
                    }
                    // 스핀 킥까지 끝난 최종 속도를 흔든다 — 그래야 결과가 벽 안쪽임을 보장할 수 있다.
                    if (RandomBounceSpreadDeg > 0 && !randomized)
                    {
                        RandomizeBounce(new Vector2(-Math.Sign(dx), 0));
                        randomized = true;
                    }
                    collided = true;
                }
            }

            // ── Y 축 ────────────────────────────────
            double dy = Velocity.Y * subDt;
            if (dy != 0)
            {
                double tryY = Position.Y + dy;
                if (Area.IsRectValid(RectFor(Position.X, tryY)))
                {
                    Position = Position.WithY(tryY);
                }
                else
                {
                    double contactY = ResolveContact(dy, isXAxis: false);
                    Position = Position.WithY(contactY);
                    double impactY = Math.Abs(Velocity.Y);
                    if (impactY > maxImpact)
                    {
                        maxImpact = impactY;
                        normal = new Vector2(0, -Math.Sign(dy)); // 진행 반대 = 안쪽
                        hitPos = Position;                        // 벽에 닿은 그 지점
                    }
                    Velocity = Velocity.WithY(-Velocity.Y * EffRestitution);
                    if (Propelled) Propulsion = Propulsion.WithY(-Propulsion.Y);
                    // 스핀이 벽을 물어 접선(가로) 방향으로 튀고, 스핀은 소모된다.
                    //
                    // 무한 튕기기에서는 이 가산을 하지 않는다. 평소에는 감속이 곧바로 먹어치워
                    // 티가 안 나지만, 감속이 0 이면 더해진 속도가 영구히 남아 던진 것보다 빨라진다
                    // (측정: 1500 으로 던져도 스핀 때문에 1966 까지 올라가 고정됐다).
                    // 이 모드의 약속은 "힘을 잃지도 얻지도 않는다" 이므로 잃는 쪽만이 아니라 얻는 쪽도 막는다.
                    if (Math.Abs(AngularVelocity) > 1e-3)
                    {
                        if (!_settings.InfiniteBounce)
                            Velocity = Velocity.WithX(Velocity.X + AngularVelocity * _settings.SpinWallKick);
                        AngularVelocity *= _settings.SpinWallRetain;
                    }
                    if (RandomBounceSpreadDeg > 0 && !randomized)
                    {
                        RandomizeBounce(new Vector2(0, -Math.Sign(dy)));
                        randomized = true;
                    }
                    collided = true;
                }
            }
        }

        // 3.2) 중력: 바닥에 붙었을 때 처리 — 느린 수직 속도 제거(미세 바운스 방지) + 구름 마찰
        if (gravity && IsGrounded())
        {
            if (Math.Abs(Velocity.Y) < LandStopSpeed)
                Velocity = Velocity.WithY(0);
            Velocity = Velocity.WithX(Velocity.X * Math.Exp(-GroundFriction * dt));
        }

        // 3.5) 스핀: 마그누스로 궤적을 휘게 + 각속도 감쇠 + 회전각 적분
        if (Math.Abs(AngularVelocity) > 1e-3 && Velocity.LengthSquared > 1.0)
        {
            double speed = Velocity.Length;
            // 속도에 수직인 방향으로 휘게(마그누스)
            Vector2 perp = new Vector2(-Velocity.Y, Velocity.X) / speed;
            double curveAccel = _settings.MagnusStrength * AngularVelocity * speed;
            Velocity += perp * (curveAccel * dt);
        }
        SpinAngle += AngularVelocity * dt;
        AngularVelocity *= Math.Exp(-_settings.SpinFriction * dt);
        if (Math.Abs(AngularVelocity) < _settings.SpinStopThreshold)
            AngularVelocity = 0;

        // 4) 저속 정지(진동 방지). 단, 표면 스핀이 남아 있으면(끌어치기 반전 지점 등)
        //    아직 가속할 힘이 있으므로 속도를 죽이지 않는다.
        //    중력이 있으면 "바닥에 있을 때"만 정지시킨다(포물선 정점의 순간 저속을 정지로 오인 방지).
        bool sleeping = false;
        // 자동 이동 중에는 임계 아래여도 죽이지 않는다 — 꼬물 속도가 임계보다 느리다.
        if (!AutoMoving
            && Velocity.Length < _settings.StopThreshold
            && Math.Abs(SurfaceSpin) < _settings.SurfaceSpinStopThreshold
            && (!gravity || IsGrounded()))
        {
            Velocity = Vector2.Zero;
            sleeping = true;
        }
        // 아직 회전 중이거나 표면 스핀이 남아 있으면 수면 아님
        if (Math.Abs(AngularVelocity) >= _settings.SpinStopThreshold
            || Math.Abs(SurfaceSpin) >= _settings.SurfaceSpinStopThreshold)
            sleeping = false;

        return new PhysicsStepResult
        {
            Collided = collided,
            MaxImpactSpeed = maxImpact,
            Sleeping = sleeping,
            CollisionNormal = normal,
            CollisionPosition = hitPos,
        };
    }

    /// <summary>
    /// 반사가 끝난 속도를 벽 법선 기준으로 무작위 각도만큼 회전시킨다(입사각=반사각 깨기).
    ///
    /// 회전이라 속도 크기가 보존된다 — 반발 계수로 이미 줄어든 양은 그대로 두고 방향만 바꾸므로,
    /// 무한 튕기기의 "힘을 얻지도 잃지도 않는다" 약속이 유지된다.
    ///
    /// 뽑는 범위는 [반사각 ± 흔들기] 와 [벽면에서 최소 <see cref="MinExitAngleDeg"/> 이상 떨어진 방향]
    /// 의 교집합이다. 순서가 중요하다 — 넓게 뽑고 나서 자르면 비스듬히 스친 충돌마다
    /// 결과가 최소 이탈각 한 값에 몰려 매번 같은 각도로 튀어나온다. 범위를 먼저 좁히고 그 안에서 뽑는다.
    ///
    /// normal 은 플레이 영역 안쪽을 향하는 단위 벡터이고, 축 분리 반사라 항상 (±1,0) 또는 (0,±1) 이다.
    /// </summary>
    private void RandomizeBounce(Vector2 normal)
    {
        double speed = Velocity.Length;
        if (speed < 1e-6) return;

        // 법선 기준 좌표계: along = 벽에서 멀어지는 성분, tang = 벽을 따라가는 성분.
        Vector2 tangent = new(-normal.Y, normal.X);
        double along = Velocity.X * normal.X + Velocity.Y * normal.Y;
        double tang = Velocity.X * tangent.X + Velocity.Y * tangent.Y;

        double limit = (90.0 - MinExitAngleDeg) * Math.PI / 180.0;
        double spread = RandomBounceSpreadDeg * Math.PI / 180.0;
        double phi = Math.Atan2(tang, along);   // 반사 방향이 법선과 이루는 각(부호 있음)

        // 벽을 따라 가던 쪽(좌우 또는 상하)은 그대로 두고 각도만 흔든다. 부호까지 뒤집히면
        // 오른쪽으로 날아가던 공이 바닥을 맞고 왼쪽으로 되돌아와, 튕긴 게 아니라 되돌아온 것처럼 보인다.
        // 정면에 가깝게 맞으면 따라가던 쪽이랄 게 없으므로 좌우 중 무작위로 정한다.
        double sign = Math.Abs(phi) < HeadOnDeg * Math.PI / 180.0
            ? (_rng.Next(2) == 0 ? -1.0 : 1.0)
            : Math.Sign(phi);

        double lo = sign > 0 ? Math.Max(phi - spread, 0.0) : Math.Max(phi - spread, -limit);
        double hi = sign > 0 ? Math.Min(phi + spread, limit) : Math.Min(phi + spread, 0.0);
        // 반사 방향 자체가 이미 허용 범위 밖(벽을 거의 스침)이면 가장 가까운 합법 각으로 붙인다.
        double theta = hi > lo ? lo + _rng.NextDouble() * (hi - lo)
                              : Math.Clamp(phi, -limit, limit);

        Velocity = (normal * Math.Cos(theta) + tangent * Math.Sin(theta)) * speed;
    }

    /// <summary>
    /// 현재 위치(유효)에서 delta 만큼 이동하면 무효가 될 때,
    /// 벽에 최대한 근접한 이동량을 이분 탐색으로 구해 접촉 좌표를 반환한다.
    /// </summary>
    private double ResolveContact(double delta, bool isXAxis)
    {
        double lo = 0.0;   // 유효
        double hi = delta; // 무효
        for (int k = 0; k < 6; k++)
        {
            double mid = (lo + hi) * 0.5;
            Rect r = isXAxis
                ? RectFor(Position.X + mid, Position.Y)
                : RectFor(Position.X, Position.Y + mid);
            if (Area.IsRectValid(r)) lo = mid;
            else hi = mid;
        }
        return (isXAxis ? Position.X : Position.Y) + lo;
    }

    /// <summary>
    /// 무효한 자리에서 가장 가까운 유효 자리를 고리를 넓혀가며 찾는다.
    /// 가까운 고리부터 훑으므로 먼저 걸린 것이 사실상 최근접이다.
    /// </summary>
    private bool TryFindValidNear(Vector2 from, out Vector2 found)
    {
        found = from;
        if (Area.IsRectValid(RectFor(from.X, from.Y))) return true;

        const double step = 4.0;
        const double maxRadius = 480.0;
        for (double r = step; r <= maxRadius; r += step)
        {
            for (int deg = 0; deg < 360; deg += 12)
            {
                double a = deg * Math.PI / 180.0;
                double x = from.X + Math.Cos(a) * r;
                double y = from.Y + Math.Sin(a) * r;
                if (Area.IsRectValid(RectFor(x, y)))
                {
                    found = new Vector2(x, y);
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>드래그 등 직접 배치 시 유효한 자리에 놓는다.
    ///
    /// 가상 데스크톱 사각형은 모니터들의 합집합이 아니다 — 높이가 어긋난 모니터가 하나라도 있으면
    /// 위쪽에 어느 모니터에도 속하지 않는 띠가 생긴다(실측: 3번 모니터가 19px 위로 어긋난 배치에서
    /// y −19..0 구간). 거기에 놓인 공은 어느 축으로도 유효한 이동이 없어 <see cref="ResolveContact"/>
    /// 가 매번 0 을 돌려주고, 그 자리에 박힌 채 반발로 속도만 죽는다.
    /// 그래서 사각형으로 자른 뒤 반드시 유효한 자리까지 끌어온다.</summary>
    public void SetPositionClamped(Vector2 pos)
    {
        Rect vb = Area.VirtualBounds;
        double x = Math.Clamp(pos.X, vb.Left, Math.Max(vb.Left, vb.Right - _settings.SlimeSize));
        double y = Math.Clamp(pos.Y, vb.Top, Math.Max(vb.Top, vb.Bottom - _settings.SlimeSize));
        var clamped = new Vector2(x, y);
        Position = TryFindValidNear(clamped, out Vector2 valid) ? valid : clamped;
    }
}
