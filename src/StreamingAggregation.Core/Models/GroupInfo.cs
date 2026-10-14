namespace StreamingAggregation.Core.Models;

/// <summary>
/// 群组信息。
/// </summary>
public sealed record GroupInfo
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

    /// <summary>群组名称。</summary>
    public required string GroupName { get; init; }
}
