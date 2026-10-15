using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Core.Abstractions;

public sealed record LlmResult
{
    public bool IsSuccess { get; init; }
    public string RawResponse { get; init; } = string.Empty;
    public Digest? Digest { get; init; }

    /// <summary>
    /// 本轮提取出的行动项。
    /// </summary>
    /// <remarks>
    /// 用列表而不是单个可空对象：消费点可以直接 <c>foreach</c>，不必先判空再逐个判空。
    /// 没有行动项时是空列表而非 <see langword="null"/>。
    /// </remarks>
    public IReadOnlyList<ActionItem> ActionItems { get; init; } = Array.Empty<ActionItem>();
}
