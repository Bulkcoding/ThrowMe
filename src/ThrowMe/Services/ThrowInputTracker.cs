using ThrowMe.Models;
using ThrowMe.Physics;

namespace ThrowMe.Services;

/// <summary>
/// 드래그 중 마우스 위치 샘플을 모아 놓는 순간의 투척 속도를 계산한다.
/// 한 프레임 이동량이 아니라 최근 시간창(ThrowSampleWindowMs) 안의
/// 샘플들을 이용해 안정적인 속도를 산출한다.
/// 좌표/시간 단위: 물리 픽셀, 초.
/// </summary>
public sealed class ThrowInputTracker
{
    private readonly AppSettings _settings;

    private readonly struct Sample
    {
        public readonly Vector2 Position;
        public readonly double Time;
        public Sample(Vector2 position, double time)
        {
            Position = position;
            Time = time;
        }
    }

    // 링버퍼(고정 용량). 시간창을 넉넉히 담을 크기.
    private const int Capacity = 16;
    private readonly Sample[] _buffer = new Sample[Capacity];
    private int _count;
    private int _head; // 다음에 쓸 위치

    public ThrowInputTracker(AppSettings settings) => _settings = settings;

    public void Reset()
    {
        _count = 0;
        _head = 0;
    }

    /// <summary>드래그 중 매 이동마다 호출.</summary>
    public void AddSample(Vector2 position, double timeSeconds)
    {
        _buffer[_head] = new Sample(position, timeSeconds);
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;
    }

    /// <summary>
    /// 놓는 순간의 투척 속도(px/s). 시간창 안의 가장 오래된 샘플과
    /// 최신 샘플의 변위/시간으로 계산하고 상한으로 클램프한다.
    /// </summary>
    public Vector2 ComputeThrowVelocity(double nowSeconds)
    {
        if (_count < 2)
            return Vector2.Zero;

        double windowSec = _settings.ThrowSampleWindowMs / 1000.0;
        double cutoff = nowSeconds - windowSec;

        // 최신 샘플
        int newestIdx = (_head - 1 + Capacity) % Capacity;
        Sample newest = _buffer[newestIdx];

        // 시간창 안에서 가장 오래된 샘플 탐색
        Sample oldest = newest;
        bool found = false;
        for (int i = 0; i < _count; i++)
        {
            int idx = (_head - 1 - i + Capacity * 2) % Capacity;
            Sample s = _buffer[idx];
            if (s.Time >= cutoff)
            {
                oldest = s;
                found = true;
            }
            else
            {
                break; // 더 과거는 창 밖
            }
        }

        // 창 안 샘플이 하나뿐이면 창 밖 직전 샘플로 보강
        if (!found || oldest.Time == newest.Time)
        {
            int secondIdx = (_head - 2 + Capacity) % Capacity;
            oldest = _buffer[secondIdx];
        }

        double dt = newest.Time - oldest.Time;
        if (dt <= 1e-4)
            return Vector2.Zero;

        Vector2 velocity = (newest.Position - oldest.Position) / dt * _settings.ThrowPower;
        return velocity.ClampLength(_settings.EffectiveMaxThrowSpeed);
    }
}
