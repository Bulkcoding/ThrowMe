namespace ThrowMe.Models;

/// <summary>슬라임이 스스로 움직이는 방식. 무한 튕기기와 달리 느리고 끊임없이 움직인다.</summary>
public enum AutoMoveMode
{
    /// <summary>끔(기본). 던지지 않으면 가만히 있는다.</summary>
    Off = 0,

    /// <summary>화면을 느릿느릿 기어다닌다. 방향은 시간이 지나며 조금씩 바뀐다.</summary>
    Roam = 1,

    /// <summary>중력을 켜서 화면 아래(작업표시줄 위)에 붙어 좌우로만 걸어다닌다.</summary>
    Taskbar = 2,

    /// <summary>마우스 커서의 오른쪽 아래를 목표로 천천히 따라다닌다.</summary>
    CursorFollow = 3,
}

/// <summary>커서 따라가기의 방식.</summary>
public enum CursorFollowStyle
{
    /// <summary>따라오기 — 커서 쪽으로 한 걸음씩 뛰어 쫓아온다.</summary>
    Follow = 0,

    /// <summary>키링 — 커서에 매달려 흔들린다. 커서를 움직이면 뒤로 늘어졌다가 아래로 모인다.</summary>
    Keyring = 1,
}
