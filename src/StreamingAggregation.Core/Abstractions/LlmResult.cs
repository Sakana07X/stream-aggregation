using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Core.Abstractions;

public sealed record LlmResult
{
    public bool IsSuccess { get; init; }
    public string RawResponse { get; init; } = string.Empty;
    public Digest? Digest { get; init; }
}
