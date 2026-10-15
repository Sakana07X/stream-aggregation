using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Core.Abstractions;

public interface IMessageStore
{
    Task SaveAsync(Message message, CancellationToken cancellationToken = default);
    Task<Message?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Message>> GetByGroupAsync(string groupId, DateTime since, CancellationToken cancellationToken = default);
    Task CleanupAsync(TimeSpan retention, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取最近写入的消息，按时间降序（新的在前）。不限群组，供消息页展示最近消息列表。
    /// </summary>
    /// <param name="limit">最多返回多少条；超出 1–1000 的取值会被钳制到该区间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<Message>> GetRecentAsync(int limit, CancellationToken cancellationToken = default);
}
