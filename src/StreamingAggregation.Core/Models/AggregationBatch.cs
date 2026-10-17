using StreamingAggregation.Core.Diagnostics;

namespace StreamingAggregation.Core.Models;

/// <summary>
/// 聚合批次：一个时间窗口内某群组的待处理消息集合。
/// </summary>
public sealed record AggregationBatch
{
    /// <summary>批次唯一标识。</summary>
    public required string Id
    {
        get => field;
        init
        {
            Guard.NotNullOrWhiteSpace(value, nameof(Id));
            field = value;
        }
    }

    /// <summary>所属群组标识。</summary>
    public required string GroupId
    {
        get => field;
        init
        {
            Guard.NotNullOrWhiteSpace(value, nameof(GroupId));
            field = value;
        }
    }

    /// <summary>所属群组名称。</summary>
    public required string GroupName { get; init; }

    /// <summary>批次内消息列表。</summary>
    public required IReadOnlyList<Message> Messages { get; init; }

    /// <summary>
    /// 聚合窗口开始时间。
    /// TODO(T3): 替换为 IClock 注入，移除 DateTime.UtcNow 占位。
    /// </summary>
    public DateTime WindowStart { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// 聚合窗口结束时间。
    /// TODO(T3): 替换为 IClock 注入，移除 DateTime.UtcNow 占位。
    /// </summary>
    public DateTime WindowEnd { get; init; } = DateTime.UtcNow;

    /// <summary>批次状态。</summary>
    public required BatchStatus Status { get; init; }
}
