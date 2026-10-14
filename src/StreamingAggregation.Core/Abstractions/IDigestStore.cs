using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Core.Abstractions;

public interface IDigestStore
{
    Task SaveAsync(Digest digest, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Digest>> GetByBatchAsync(string batchId, CancellationToken cancellationToken = default);
}
