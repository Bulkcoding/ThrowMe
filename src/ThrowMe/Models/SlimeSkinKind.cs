namespace ThrowMe.Models;

/// <summary>슬라임 창에 표시할 스킨 종류. 새 스킨은 여기에 추가하고 대응 UserControl 을 만든다.</summary>
public enum SlimeSkinKind
{
    /// <summary>기본 민트/보라 젤리 슬라임.</summary>
    Jelly = 0,

    /// <summary>당구공(반들반들 8-ball) — "당구공처럼 튕긴다" 컨셉.</summary>
    Billiard = 1,

    /// <summary>몬스터볼 — 클릭 시 열리는 이펙트.</summary>
    Pokeball = 2,

    /// <summary>하이퍼볼(울트라볼) — 검정+노랑.</summary>
    Ultra = 3,

    /// <summary>마스터볼 — 보라+분홍 M.</summary>
    Master = 4,

    /// <summary>농구공 — 주황 구체에 검정 씸. 튈 때마다 무늬 변경 + 골대 넣기.</summary>
    Basketball = 5,

    /// <summary>볼링공 — 파란 마블. 굴러가며 9가지 무늬(손가락 구멍 배치)가 바뀐다.</summary>
    Bowling = 6,

    /// <summary>종이비행기 — 접힌 면이 보이는 흰 종이비행기. 날아가는 물체 컨셉.</summary>
    PaperPlane = 7,

    /// <summary>펫 — 가져온 Codex Pet 스프라이트시트(AppSettings.PetId). CLI 상태에 따라 동작이 바뀐다.</summary>
    Pet = 8,
}
