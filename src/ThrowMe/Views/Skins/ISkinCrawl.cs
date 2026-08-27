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
    /// <param name="lunge">뻗기 세기 0~1. 0이면 모은 돔, 1이면 앞으로 쭉 뻗은 모양.</param>
    /// <param name="dirX">진행 방향. +1 이면 오른쪽, -1 이면 왼쪽(꼬리가 반대편에 생긴다).</param>
    void SetCrawlPose(double lunge, double dirX);

    /// <summary>평소의 동그란 모양으로 되돌린다.</summary>
    void ClearCrawlPose();
}
