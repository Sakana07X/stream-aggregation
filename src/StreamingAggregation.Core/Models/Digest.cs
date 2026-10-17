using StreamingAggregation.Core.Diagnostics;

namespace StreamingAggregation.Core.Models;

/// <summary>
/// 聚合摘要：对一批消息的归纳结果。
/// </summary>
public sealed record Digest
{
    /// <summary>摘要唯一标识。</summary>
    public required string Id
    {
        get => field;
        init
        {
            Guard.NotNullOrWhiteSpace(value, nameof(Id));
            field = value;
        }
    }

    /// <summary>来源聚合批次标识。</summary>
    public required string BatchId
    {
        get => field;
        init
        {
            Guard.NotNullOrWhiteSpace(value, nameof(BatchId));
            field = value;
        }
    }

    /// <summary>摘要分类。</summary>
    public required DigestCategory Category { get; init; }

    /// <summary>摘要正文。</summary>
    public required string Summary { get; init; }

    /// <summary>归类理由。</summary>
    public required string Reason { get; init; }

    /// <summary>
    /// 创建时间。
    /// TODO(T3): 替换为 IClock 注入，移除 DateTime.UtcNow 占位。
    /// </summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
