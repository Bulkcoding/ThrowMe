namespace ThrowMe.Views.Skins;

/// <summary>
/// 기어다니는 자세를 <b>실루엣 자체</b>로 표현하는 스킨.
///
/// 스케일 변형(ScaleTransform)으로는 그냥 찌그러진 타원이 될 뿐이라,
/// 바닥에 눌린 돔 + 뒤로 끌리는 꼬리 같은 모양이 나오지 않는다.
/// 그래서 이 인터페이스를 구현한 스킨은 몸통 도형을 매 프레임 다시 그린다.
/// </summary>
public interface ISkinCrawl
{
    /// <summary>
    /// 기어다니는 자세를 만든다.
    /// </summary>
    /// <param name="t">걸음 주기 안의 위치 0~1. 이 값이 곧 몇 번째 컷을 보여줄지가 된다.</param>
    /// <param name="faceRight">바라보는 쪽. 좌우 전용 컷이 따로 있으므로 그림을 뒤집지 않는다.</param>
    void SetCrawlPose(double t, bool faceRight);

    /// <summary>평소의 동그란 모양으로 되돌린다.</summary>
    void ClearCrawlPose();
}
