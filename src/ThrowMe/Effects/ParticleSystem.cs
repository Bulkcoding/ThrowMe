using ThrowMe.Models;
using ThrowMe.Physics;

namespace ThrowMe.Effects;

/// <summary>단일 파티클 상태(물리 픽셀 좌표).</summary>
public struct Particle
{
    public Vector2 Position;
    public Vector2 Velocity;
    public double Age;       // 경과 시간(초)
    public double LifeSpan;  // 총 수명(초)
    public double Size;      // px
    public ImpactTier Tier;  // 색/스타일 결정용(디자인 트랙)
    public bool Spark;       // true 면 당구 쿠션 스파크(밝은 색·작음)

    /// <summary>남은 수명 비율 1→0.</summary>
    public readonly double LifeFraction => LifeSpan <= 0 ? 0 : Math.Clamp(1.0 - Age / LifeSpan, 0, 1);
}

/// <summary>
/// 충돌·펀치 시 튀는 파티클을 시뮬레이션하는 순수 로직(렌더 비의존).
/// 좌표는 물리 픽셀. 렌더는 ParticleOverlayWindow(디자인 교체 가능)가 담당.
/// </summary>
public sealed class ParticleSystem
{
    private readonly AppSettings _settings;
    private readonly List<Particle> _particles = new(64);

    // 결정적 재현이 필요 없는 시각 효과이므로 런타임 Random 사용.
    private readonly Random _rng = new();

    public ParticleSystem(AppSettings settings) => _settings = settings;

    public IReadOnlyList<Particle> Active => _particles;
    public bool HasActive => _particles.Count > 0;

    /// <summary>활성 파티클이 퍼질 수 있는 최대 범위(물리 px).
    ///
    /// 파티클도 고정 크기(1300px) 오버레이 창 하나가 그리므로, 멀리 떨어진 두 번째 폭발이
    /// 생기면 한쪽이 창 밖으로 잘린다(창 리사이즈는 레이어드 창에서 동기 스톨 → 금지).
    /// 공이 빠를수록 양쪽 벽을 번갈아 때려 잦아진다 → 새 폭발이 범위를 넘기면 기존 것을 비운다.
    /// (1300 − 파티클 여유 ≈ 900)</summary>
    public const double MaxSpreadPx = 900.0;

    /// <summary>origin 에서 새로 방출하기 전, 퍼짐 한계를 넘으면 기존 파티클을 비운다.</summary>
    private void EnsureSpread(Vector2 origin)
    {
        if (_particles.Count == 0) return;

        double minX = origin.X, maxX = origin.X;
        double minY = origin.Y, maxY = origin.Y;
        foreach (Particle p in _particles)
        {
            if (p.Position.X < minX) minX = p.Position.X;
            if (p.Position.X > maxX) maxX = p.Position.X;
            if (p.Position.Y < minY) minY = p.Position.Y;
            if (p.Position.Y > maxY) maxY = p.Position.Y;
        }
        if ((maxX - minX) > MaxSpreadPx || (maxY - minY) > MaxSpreadPx)
            _particles.Clear();
    }

    /// <summary>충돌/펀치 지점에서 파티클 방출.</summary>
    /// <param name="origin">방출 중심(물리 픽셀).</param>
    /// <param name="intensity01">0~1 세기(개수·속도 스케일).</param>
    public void Emit(Vector2 origin, double intensity01, ImpactTier tier)
    {
        if (!_settings.ParticlesEnabled) return;

        intensity01 = Math.Clamp(intensity01, 0, 1);
        int count = (int)Math.Round(
            _settings.ParticleBaseCount +
            (_settings.ParticleMaxCount - _settings.ParticleBaseCount) * intensity01);
        if (count <= 0) return;
        EnsureSpread(origin);

        double speedMin = _settings.ParticleSpeedMin;
        double speedMax = _settings.ParticleSpeedMax;

        for (int i = 0; i < count; i++)
        {
            double angle = _rng.NextDouble() * Math.PI * 2.0;
            // 세게 부딪힐수록 빠르게. 위쪽으로 살짝 편향(중력에 저항하며 튀는 느낌).
            double speed = (speedMin + _rng.NextDouble() * (speedMax - speedMin)) * (0.5 + 0.5 * intensity01);
            var vel = new Vector2(Math.Cos(angle) * speed, Math.Sin(angle) * speed - speed * 0.35);

            _particles.Add(new Particle
            {
                Position = origin,
                Velocity = vel,
                Age = 0,
                LifeSpan = _settings.ParticleLifeSeconds * (0.7 + _rng.NextDouble() * 0.6),
                Size = _settings.ParticleSize * (0.7 + _rng.NextDouble() * 0.6),
                Tier = tier,
            });
        }
    }

    /// <summary>
    /// 당구 쿠션 충돌 스파크. 벽면(법선의 접선)을 따라 양방향으로 튀며 살짝 안쪽으로 향한다.
    /// 짧고 빠르고 작은 밝은 입자 → "탁!" 하고 쿠션에 부딪히는 느낌.
    /// </summary>
    public void EmitCushion(Vector2 origin, Vector2 normal, double intensity01, ImpactTier tier)
    {
        if (!_settings.ParticlesEnabled) return;

        intensity01 = Math.Clamp(intensity01, 0, 1);
        if (normal.LengthSquared < 1e-6) normal = new Vector2(0, -1);
        normal = normal.Normalized();
        var tangent = new Vector2(-normal.Y, normal.X); // 벽면 방향

        int count = (int)Math.Round(
            _settings.ParticleBaseCount +
            (_settings.ParticleMaxCount - _settings.ParticleBaseCount) * intensity01);
        if (count <= 0) return;
        EnsureSpread(origin);

        for (int i = 0; i < count; i++)
        {
            double along = _rng.NextDouble() * 2.0 - 1.0;      // 접선 -1..1(양방향)
            double outward = 0.12 + _rng.NextDouble() * 0.55;   // 벽에서 살짝 안쪽으로
            double speed = (_settings.ParticleSpeedMin + _rng.NextDouble() *
                            (_settings.ParticleSpeedMax - _settings.ParticleSpeedMin)) *
                           (0.7 + 0.6 * intensity01);

            Vector2 dir = (tangent * along + normal * outward).Normalized();
            _particles.Add(new Particle
            {
                Position = origin,
                Velocity = dir * speed,
                Age = 0,
                LifeSpan = _settings.ParticleLifeSeconds * 0.5 * (0.6 + _rng.NextDouble() * 0.5),
                Size = _settings.ParticleSize * 0.65 * (0.7 + _rng.NextDouble() * 0.5),
                Tier = tier,
                Spark = true,
            });
        }
    }

    /// <summary>
    /// 몬스터볼이 열리는 순간의 방사형 빛 입자. 사방으로 밝게 터지며 위로 살짝 편향.
    /// </summary>
    public void EmitOpen(Vector2 origin, double intensity01)
    {
        if (!_settings.ParticlesEnabled) return;

        intensity01 = Math.Clamp(intensity01, 0, 1);
        int count = (int)Math.Round(
            _settings.ParticleBaseCount +
            (_settings.ParticleMaxCount - _settings.ParticleBaseCount) * intensity01);
        if (count <= 0) return;
        EnsureSpread(origin);

        for (int i = 0; i < count; i++)
        {
            double angle = _rng.NextDouble() * Math.PI * 2.0;
            double speed = (_settings.ParticleSpeedMin + _rng.NextDouble() *
                            (_settings.ParticleSpeedMax - _settings.ParticleSpeedMin)) *
                           (0.6 + 0.7 * intensity01);
            var vel = new Vector2(Math.Cos(angle) * speed, Math.Sin(angle) * speed - speed * 0.25);

            _particles.Add(new Particle
            {
                Position = origin,
                Velocity = vel,
                Age = 0,
                LifeSpan = _settings.ParticleLifeSeconds * 0.8 * (0.6 + _rng.NextDouble() * 0.6),
                Size = _settings.ParticleSize * (0.8 + _rng.NextDouble() * 0.7),
                Tier = ImpactTier.Bonk,
                Spark = true, // 밝게 렌더
            });
        }
    }

    /// <summary>
    /// 종이비행기 바람. 지정 방향으로 짧고 빠르게 흐르는 입자 몇 개 —
    /// 커서로 밀 때와 단축키로 아래에서 바람을 쏠 때 같이 쓴다.
    /// </summary>
    public void EmitWind(Vector2 origin, Vector2 dir, double intensity01)
    {
        if (!_settings.ParticlesEnabled) return;
        if (dir.LengthSquared < 1e-6) return;

        intensity01 = Math.Clamp(intensity01, 0, 1);
        dir = dir.Normalized();
        var tangent = new Vector2(-dir.Y, dir.X);
        int count = 3 + (int)Math.Round(5 * intensity01);
        EnsureSpread(origin);

        for (int i = 0; i < count; i++)
        {
            double spread = (_rng.NextDouble() * 2.0 - 1.0) * 0.42;  // 부채꼴로 퍼짐
            double speed = (620 + _rng.NextDouble() * 520) * (0.6 + 0.6 * intensity01);
            Vector2 v = (dir + tangent * spread).Normalized() * speed;

            _particles.Add(new Particle
            {
                Position = origin + tangent * ((_rng.NextDouble() * 2.0 - 1.0) * 18.0),
                Velocity = v,
                Age = 0,
                LifeSpan = 0.24 * (0.7 + _rng.NextDouble() * 0.6),
                Size = _settings.ParticleSize * 0.55 * (0.7 + _rng.NextDouble() * 0.5),
                Tier = ImpactTier.Bonk,
                Spark = true, // 밝고 작게 → 바람 결처럼 보인다
            });
        }
    }

    /// <summary>deltaTime 진행. 수명이 다한 파티클 제거. 살아있는 게 있으면 true.</summary>
    public bool Update(double dt)
    {
        if (_particles.Count == 0) return false;

        double gravity = _settings.ParticleGravity;

        for (int i = _particles.Count - 1; i >= 0; i--)
        {
            Particle p = _particles[i];
            p.Age += dt;
            if (p.Age >= p.LifeSpan)
            {
                // 스왑 후 말단 제거(O(1))
                _particles[i] = _particles[^1];
                _particles.RemoveAt(_particles.Count - 1);
                continue;
            }
            p.Velocity = new Vector2(p.Velocity.X, p.Velocity.Y + gravity * dt);
            p.Position = p.Position + p.Velocity * dt;
            _particles[i] = p;
        }

        return _particles.Count > 0;
    }

    public void Clear() => _particles.Clear();
}
