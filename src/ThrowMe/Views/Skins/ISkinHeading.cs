namespace ThrowMe.Views.Skins;

/// <summary>
/// 진행 방향을 자세로 표현하는 스킨(종이비행기).
/// 굴러가는 회전(SpinAngle) 대신 좌우 뒤집기 + 기수 각도로 방향을 보여 준다.
/// </summary>
public interface ISkinHeading
{
    /// <summary>진행 방향(deg, 화면 좌표에서 atan2(vy, vx))에 맞춰 자세를 잡는다.</summary>
    void SetHeading(double travelAngleDeg);

    /// <summary>기본 자세로 되돌린다(좌/우 방향은 유지).</summary>
    void SetRestPose();
}
