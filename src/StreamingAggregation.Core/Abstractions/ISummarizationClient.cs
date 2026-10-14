using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Core.Abstractions;

public interface ISummarizationClient
{
    Task<LlmResult> SummarizeAsync(AggregationBatch batch, CancellationToken cancellationToken = default);
}
