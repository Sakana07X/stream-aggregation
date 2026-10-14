using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Core.Abstractions;

public interface IBatchStore
{
    Task SaveAsync(AggregationBatch batch, CancellationToken cancellationToken = default);
    Task<AggregationBatch?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AggregationBatch>> GetPendingAsync(CancellationToken cancellationToken = default);
}
