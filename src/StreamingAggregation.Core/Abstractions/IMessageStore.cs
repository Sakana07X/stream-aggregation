using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Core.Abstractions;

public interface IMessageStore
{
    Task SaveAsync(Message message, CancellationToken cancellationToken = default);
    Task<Message?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Message>> GetByGroupAsync(string groupId, DateTime since, CancellationToken cancellationToken = default);
    Task CleanupAsync(TimeSpan retention, CancellationToken cancellationToken = default);
}
