namespace StreamingAggregation.Core.Aggregation;

/// <summary>
/// 单个群组的聚合窗口统计信息，是 <see cref="IAggregationWindow.GetStats"/> 的返回单元。
/// <para>
/// 快照语义：所有属性均为读取时刻的值，不含时钟依赖，跨线程传递安全。
/// </para>
/// </summary>
public sealed record AggregationWindowStats
{
    /// <summary>群组唯一标识。</summary>
    public required string GroupId
    {
        get => field;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(GroupId));
            field = value;
        }
    }

    /// <summary>群组名称，未知时回落为 <see cref="GroupId"/>。</summary>
    public string GroupName { get; init; } = string.Empty;

    /// <summary>
    /// 当前最新未关闭窗口的开始时间（UTC）。
    /// 该群没有未关闭窗口时为 <see cref="DateTime"/> 默认值。
    /// </summary>
    public DateTime WindowStart { get; init; }

    /// <summary>
    /// 当前最新未关闭窗口的结束时间（UTC），等于 <see cref="WindowStart"/> 加一个窗口长度。
    /// </summary>
    public DateTime WindowEnd { get; init; }

    /// <summary>当前最新未关闭窗口内的消息数。</summary>
    public int MessageCount { get; init; }

    /// <summary>该群所有未关闭窗口内的消息数合计。</summary>
    public int PendingMessageCount { get; init; }

    /// <summary>该群当前未关闭窗口的数量（跨窗口积压时可能大于 1）。</summary>
    public int OpenWindowCount { get; init; }

    /// <summary>该群进入过窗口的消息总数（含重建恢复的消息）。</summary>
    public long TotalMessageCount { get; init; }

    /// <summary>该群已产出（到点或提前成批）的窗口数。</summary>
    public long FlushedWindowCount { get; init; }

    /// <summary>其中因触及 <see cref="MaxMessagesPerWindow"/> 而提前成批的次数。</summary>
    public long EarlyFlushCount { get; init; }

    /// <summary>该群因 <c>MessageId</c> 重复而被忽略的消息数。</summary>
    public long DuplicateCount { get; init; }

    /// <summary>单窗口消息数上限，来源于 <see cref="AggregationOptions.MaxMessagesPerWindow"/>。</summary>
    public int MaxMessagesPerWindow { get; init; } = AggregationOptions.DefaultMaxMessagesPerWindow;

    /// <summary>当前最新窗口是否已达到消息数上限。</summary>
    public bool IsAtCapacity => MessageCount >= MaxMessagesPerWindow;

    /// <summary>当前最新窗口的长度（<see cref="WindowEnd"/> 减 <see cref="WindowStart"/>）。</summary>
    public TimeSpan WindowDuration => WindowEnd > WindowStart ? WindowEnd - WindowStart : TimeSpan.Zero;
}