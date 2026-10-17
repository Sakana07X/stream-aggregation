using StreamingAggregation.Core.Diagnostics;

namespace StreamingAggregation.Core.Aggregation;

/// <summary>
/// 聚合窗口配置。
/// <para>
/// 时间窗口长度以“分钟”为单位，默认 3 分钟；
/// 单个窗口内的消息数上限用于防止高频群把内存和下游摘要接口打爆。
/// </para>
/// </summary>
public sealed record AggregationOptions
{
    /// <summary>默认窗口长度（分钟）。</summary>
    public const int DefaultWindowMinutes = 3;

    /// <summary>默认单窗口消息数上限。</summary>
    public const int DefaultMaxMessagesPerWindow = 200;

    private const int MinWindowMinutes = 1;
    private const int MaxWindowMinutes = 24 * 60;
    private const int MinMessagesPerWindow = 1;
    private const int MaxMessagesPerWindowLimit = 100_000;

    /// <summary>默认配置（3 分钟窗口，每窗口 200 条消息）。</summary>
    public static AggregationOptions Default { get; } = new();

    /// <summary>
    /// 窗口长度（分钟）。默认 <see cref="DefaultWindowMinutes"/>。
    /// </summary>
    public int WindowMinutes
    {
        get => field;
        init
        {
            Guard.ThrowIfLessThan(value, MinWindowMinutes);
            Guard.ThrowIfGreaterThan(value, MaxWindowMinutes);
            field = value;
        }
    } = DefaultWindowMinutes;

    /// <summary>
    /// 单个窗口内允许累积的最大消息数。默认 <see cref="DefaultMaxMessagesPerWindow"/>。
    /// <para>
    /// 到达上限后立即提前成批（不丢消息），后续消息进入下一个批次。
    /// </para>
    /// </summary>
    public int MaxMessagesPerWindow
    {
        get => field;
        init
        {
            Guard.ThrowIfLessThan(value, MinMessagesPerWindow);
            Guard.ThrowIfGreaterThan(value, MaxMessagesPerWindowLimit);
            field = value;
        }
    } = DefaultMaxMessagesPerWindow;

    /// <summary>窗口长度（<see cref="TimeSpan"/> 表示）。</summary>
    public TimeSpan WindowDuration => TimeSpan.FromMinutes(WindowMinutes);
}