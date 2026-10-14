namespace StreamingAggregation.Core.Abstractions;

/// <summary>
/// 时钟接口，用于获取当前时间。
/// 在测试中使用 FakeClock 替代真实时间源。
/// </summary>
public interface IClock
{
    /// <summary>
    /// 获取当前时间。
    /// </summary>
    DateTime Now { get; }
}
