using StreamingAggregation.Core.Diagnostics;

namespace StreamingAggregation.Core.Models;

/// <summary>
/// 行动项：从消息中提取的需要执行的动作。
/// </summary>
public sealed record ActionItem
{
    /// <summary>行动项唯一标识。</summary>
    public required string Id
    {
        get => field;
        init
        {
            Guard.NotNullOrWhiteSpace(value, nameof(Id));
            field = value;
        }
    }

    /// <summary>动作描述。</summary>
    public required string Action
    {
        get => field;
        init
        {
            Guard.NotNullOrWhiteSpace(value, nameof(Action));
            field = value;
        }
    }

    /// <summary>截止时间（可为空）。</summary>
    public DateTime? Deadline { get; init; }

    /// <summary>重要程度。</summary>
    public required Importance Importance { get; init; }

    /// <summary>提取理由。</summary>
    public required string Reason { get; init; }

    /// <summary>来源群组标识。</summary>
    public required string SourceGroupId
    {
        get => field;
        init
        {
            Guard.NotNullOrWhiteSpace(value, nameof(SourceGroupId));
            field = value;
        }
    }

    /// <summary>是否已完成。</summary>
    public bool Completed { get; init; }

    /// <summary>是否已忽略。</summary>
    public bool Ignored { get; init; }
}
