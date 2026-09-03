namespace ThrowMe.Models;

/// <summary>
/// CLI(Claude Code) 세션들을 합쳐 낸 현재 상태. 펫 테마가 이 값으로 동작을 고른다.
/// 숫자가 클수록 "사람이 봐야 할" 정도가 높다 — 기다림·오류는 어느 세션에서 나도 우선 보여 준다.
/// </summary>
public enum AgentState
{
    /// <summary>연결된 세션 없음 또는 모두 쉬는 중.</summary>
    Idle = 0,
    /// <summary>한 턴을 막 끝냈다(Stop). 몇 초 뒤 저절로 Idle 로 내려간다.</summary>
    Done = 1,
    /// <summary>프롬프트를 받아 생각하는 중(도구 실행 전).</summary>
    Thinking = 2,
    /// <summary>도구를 실행하며 작업 중.</summary>
    Working = 3,
    /// <summary>서브에이전트를 여럿 굴리는 중.</summary>
    Juggling = 4,
    /// <summary>권한 승인·질문 답변을 기다리는 중.</summary>
    Waiting = 5,
    /// <summary>도구 실패·API 오류 직후.</summary>
    Error = 6,
}
