using StreamingAggregation.Core.Abstractions;

namespace StreamingAggregation.Tests.Fakes;

/// <summary>
/// 可控时间源，用于测试。
/// 支持 SetTime 设置时间、Advance 推进时间。
/// </summary>
public class FakeClock : IClock
{
    private DateTime _currentTime;

    public FakeClock(DateTime? startTime = null)
    {
        _currentTime = startTime ?? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    }

    /// <inheritdoc />
    public DateTime Now => _currentTime;

    /// <summary>
    /// 设置当前时间。
    /// </summary>
    public void SetTime(DateTime time)
    {
        _currentTime = time;
    }

    /// <summary>
    /// 推进时间。
    /// </summary>
    public void Advance(TimeSpan duration)
    {
        _currentTime = _currentTime.Add(duration);
    }
}
