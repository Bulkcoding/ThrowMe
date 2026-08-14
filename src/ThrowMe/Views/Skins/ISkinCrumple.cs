namespace ThrowMe.Views.Skins;

/// <summary>벽에 부딪히면 구겨지고, 바닥에 닿으면 원래 모양으로 돌아오는 스킨(종이비행기).</summary>
public interface ISkinCrumple
{
    /// <summary>지금 구겨져 있는가.</summary>
    bool IsCrumpled { get; }

    /// <summary>구겨짐/펴짐 전환.</summary>
    void SetCrumpled(bool on);
}
