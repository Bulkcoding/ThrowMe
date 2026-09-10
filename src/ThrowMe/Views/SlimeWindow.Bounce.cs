using ThrowMe.Models;
using ThrowMe.Physics;
using ThrowMe.Services;

namespace ThrowMe.Views;

/// <summary>
/// 무한 튕기기 + CLI 연동: Claude Code 세션이 일하는 동안 세게, 쉬는 동안 느리게 튕긴다.
///
/// 무한 튕기기는 감속 0·반발 1 이라 속도 크기가 저절로 변하지 않는다. 그래서 매 프레임 물리 갱신 직전에
/// <b>방향은 그대로 두고 크기만</b> 목표값으로 맞춘다(즉시 반영 — 사용자가 던진 속도도 다음 프레임에 덮인다).
/// 멈춰 있던 공은 세션이 "일 시작" 상태로 바뀌는 순간에만 랜덤 방향으로 출발한다 — 잡기 단축키로 잡아 둔
/// 공이 저절로 다시 튀어 나가지 않게 하려는 것이다. 세션이 하나도 없으면 아무것도 하지 않는다(기존 동작).
/// </summary>
public partial class SlimeWindow
{
    private readonly Random _bounceRng = new();

    /// <summary>마지막으로 로그에 남긴 목표 속도(px/s, 배율 적용 전). 바뀔 때만 한 줄 남기려는 것.</summary>
    private double _bounceLoggedTarget = -1;

    /// <summary>세션이 "일하는 중"으로 보는 상태. 나머지(완료·대기·승인 대기·오류)는 쉬는 중.</summary>
    private static bool IsWorkingState(AgentState s)
        => s is AgentState.Thinking or AgentState.Working or AgentState.Juggling;

    /// <summary>
    /// 지금 속도 제어를 해야 하는 상황인가(적용 조건 + 제외 상황).
    /// 슬라임(젤리) 테마에서만 제어한다(사용자 결정). 다른 테마는 중력(농구공·종이비행기)이나 굴림·스핀 연출이
    /// 속도 크기 자체를 바꾸는데, 매 프레임 크기를 덮어쓰면 그 연출이 사라진다. 표면 스핀이 남아 있는 동안도 같은 이유로 제외.
    /// </summary>
    private bool SessionBounceActive =>
        _settings.InfiniteBounce
        && _settings.CliLinkEnabled
        && CliSessionCount > 0
        && !_isDragging
        && !_settings.Paused
        && _settings.SlimeVisible
        && (!_networked || _ownsBall)
        && _settings.Skin == SlimeSkinKind.Jelly
        && Math.Abs(_physics.SurfaceSpin) < 1e-3;

    /// <summary>현재 세션 상태의 목표 속도(px/s, 배율 적용 전).</summary>
    private double SessionBounceTargetBase =>
        IsWorkingState(_agentState) ? _settings.BounceWorkSpeed : _settings.BounceIdleSpeed;

    /// <summary>매 프레임 물리 갱신 직전에 호출. 움직이는 공의 속도 크기를 목표값으로 맞춘다.</summary>
    private void TickSessionBounce()
    {
        if (!SessionBounceActive) { _bounceLoggedTarget = -1; return; }

        double targetBase = SessionBounceTargetBase;
        if (targetBase != _bounceLoggedTarget)
        {
            Logger.Info($"SessionBounce target {targetBase:0} px/s (state={_agentState}, sessions={CliSessionCount}).");
            _bounceLoggedTarget = targetBase;
        }

        var v = _physics.Velocity;
        if (v.LengthSquared < 1e-6) return;   // 멈춘 공은 여기서 출발시키지 않는다(전환 순간에만 출발)

        double target = targetBase * _settings.DisplayScale;
        _physics.Velocity = v.Normalized() * target;
    }

    /// <summary>
    /// 세션 합산 상태가 바뀌었을 때(UI 스레드). 쉬는 중 → 일하는 중으로 넘어가는 순간, 공이 멈춰 있으면
    /// 랜덤 방향으로 출발시킨다. 그 외에는 다음 프레임의 <see cref="TickSessionBounce"/> 가 크기를 맞춘다.
    /// </summary>
    private void OnSessionBounceStateChanged(AgentState previous, AgentState current)
    {
        if (!SessionBounceActive) return;

        bool startedWorking = !IsWorkingState(previous) && IsWorkingState(current);
        if (startedWorking && _physics.IsAtRest)
        {
            // 네 사분면 중 하나를 고르고 그 안에서 20°~70° — 가로·세로 성분이 모두 있어 벽에 나란히 미끄러지지 않는다.
            double deg = _bounceRng.Next(4) * 90.0 + 20.0 + _bounceRng.NextDouble() * 50.0;
            double rad = deg * Math.PI / 180.0;
            double target = _settings.BounceWorkSpeed * _settings.DisplayScale;
            _physics.Velocity = new Vector2(Math.Cos(rad), Math.Sin(rad)) * target;
            Logger.Info($"SessionBounce launch {deg:0}° at {_settings.BounceWorkSpeed:0} px/s.");
        }

        // 멈춘 공은 렌더 루프가 자고 있다. 출발했거나 목표가 바뀌었으면 깨워서 다음 프레임에 반영한다.
        EnsureRendering();
    }
}
