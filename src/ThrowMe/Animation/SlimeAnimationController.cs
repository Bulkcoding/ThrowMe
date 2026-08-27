using System.Windows.Media;
using ThrowMe.Models;
using ThrowMe.Physics;

namespace ThrowMe.Animation;

/// <summary>
/// 슬라임의 말랑한 시각 효과(Squash/Stretch/Punch)를 코드 기반 보간으로 제어한다.
///
/// [디자인 트랙과의 계약]
/// XAML 은 반드시 다음 이름의 Transform 을 노출한다.
///   - ScaleTransform  x:Name="SlimeScale"  (CenterX/Y 는 RenderTransformOrigin 0.5,0.5 로 처리)
///   - RotateTransform x:Name="SlimeRotate"
/// 이 컨트롤러는 위 두 Transform 만 조작하므로, 비주얼을 도형→PNG→스프라이트로
/// 교체하더라도 이름만 유지하면 로직은 바뀌지 않는다.
/// </summary>
public sealed class SlimeAnimationController
{
    private readonly ScaleTransform _scale;
    private readonly RotateTransform _rotate;
    private readonly AppSettings _settings;

    // 현재/목표 스케일. 스프링으로 목표를 향해 이완된다.
    private double _curX = 1.0, _curY = 1.0;
    private double _tgtX = 1.0, _tgtY = 1.0;
    private double _curAngle;
    private double _tgtAngle;
    private double _spinAngle; // 스핀에 의한 추가 회전(deg)

    /// <summary>true 면 찌그러짐/변형 없이 형태를 고정한다(당구공 등 단단한 스킨). 스핀 회전은 유지.</summary>
    public bool Rigid { get; set; }

    /// <summary>
    /// 자동 이동(꼬물꼬물) 중 형태를 직접 지정한다. null 이면 평소처럼 속도로 계산한다.
    ///
    /// 속도 기반 stretch 는 <see cref="AppSettings.ImpactReferenceSpeed"/>(7000px/s) 대비로 계산해서,
    /// 15px/s 로 기어갈 때는 변형이 사실상 0 이 된다 — 모양 그대로 미끄러져 '떠다니는' 것처럼 보였다.
    /// 그래서 기어다닐 때는 걸음 주기가 직접 형태를 만든다.
    /// </summary>
    public (double X, double Y, double AngleDeg)? CrawlShape { get; set; }

    public SlimeAnimationController(ScaleTransform scale, RotateTransform rotate, AppSettings settings)
    {
        _scale = scale;
        _rotate = rotate;
        _settings = settings;
        Apply();
    }

    /// <summary>정지 형태에 충분히 수렴했는지(렌더 루프 유휴화 판단).</summary>
    public bool IsResting =>
        Math.Abs(_curX - 1.0) < 0.005 &&
        Math.Abs(_curY - 1.0) < 0.005 &&
        Math.Abs(_tgtX - 1.0) < 0.005 &&
        Math.Abs(_tgtY - 1.0) < 0.005;

    /// <summary>매 프레임 호출. 속도에 따른 Stretch 목표 설정 + 스프링 이완.</summary>
    public void Tick(double dt, Vector2 velocity, double spinAngleDeg)
    {
        _spinAngle = spinAngleDeg;

        if (Rigid)
        {
            // 단단한 스킨: 형태 고정, 스핀 회전만 반영
            _curX = _curY = _tgtX = _tgtY = 1.0;
            _curAngle = _tgtAngle = 0;
            Apply();
            return;
        }

        double speed = velocity.Length;

        if (CrawlShape is { } c)
        {
            // 걸음이 형태를 만든다. 속도는 보지 않는다.
            _tgtX = c.X;
            _tgtY = c.Y;
            _tgtAngle = c.AngleDeg;
        }
        else if (speed > 1.0)
        {
            // 이동 방향으로 늘어나고 수직으로 납작해진다.
            double t = Math.Clamp(speed / _settings.ImpactReferenceSpeed, 0, 1);
            double stretch = t * _settings.MaxStretch * _settings.Softness;
            _tgtX = 1.0 + stretch;   // 진행축(로컬 X) 늘림
            _tgtY = 1.0 - stretch * 0.6;
            _tgtAngle = Math.Atan2(velocity.Y, velocity.X) * 180.0 / Math.PI;
        }
        else
        {
            _tgtX = 1.0;
            _tgtY = 1.0;
            // 정지 시 회전은 현재 각도 유지(급회전 방지)
        }

        // 스프링 이완: 프레임 독립 지수 보간
        double a = 1.0 - Math.Exp(-_settings.AnimationStiffness * dt);
        _curX += (_tgtX - _curX) * a;
        _curY += (_tgtY - _curY) * a;
        _curAngle += NormalizeAngleDelta(_tgtAngle - _curAngle) * a;

        Apply();
    }

    /// <summary>벽 충돌 순간 납작해지는 Squash(즉시 팝 후 스프링 복귀).</summary>
    public void OnImpact(double impactSpeed)
    {
        if (Rigid) return; // 단단한 스킨은 찌그러지지 않음
        double t = Math.Clamp(impactSpeed / _settings.ImpactReferenceSpeed, 0, 1);
        double f = t * _settings.MaxStretch * _settings.Softness * 1.5;
        // 진행축으로 퍼지고 수직으로 눌린다.
        _curX = 1.0 + f;
        _curY = 1.0 - f;
        Apply();
    }

    /// <summary>클릭 시 짧게 찌그러지는 Punch(양축 압축 후 복귀).</summary>
    public void Punch()
    {
        if (Rigid) return; // 단단한 스킨은 찌그러지지 않음
        double f = _settings.MaxStretch * _settings.Softness;
        _curX = 1.0 - f;
        _curY = 1.0 - f;
        Apply();
    }

    /// <summary>즉시 정지 형태로 초기화(위치 리셋 등).</summary>
    public void ResetToRest()
    {
        _curX = _curY = _tgtX = _tgtY = 1.0;
        _curAngle = _tgtAngle = 0;
        _spinAngle = 0;
        Apply();
    }

    private void Apply()
    {
        _scale.ScaleX = _curX;
        _scale.ScaleY = _curY;
        _rotate.Angle = _curAngle + _spinAngle;
    }

    private static double NormalizeAngleDelta(double delta)
    {
        while (delta > 180) delta -= 360;
        while (delta < -180) delta += 360;
        return delta;
    }
}
