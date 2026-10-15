using StreamingAggregation.Core;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Tests.Fakes;

/// <summary>
/// 记录调用顺序的批次存储：BatchProcessor 的编排顺序必须能被断言，
/// 光断言"最后状态是什么"抓不到"先写行动项还是先写摘要"这类次序回归。
/// </summary>
/// <remarks>
/// 替身也尊重取消令牌：真实仓储在令牌已取消时立刻抛 <see cref="OperationCanceledException"/>，
/// 替身若无视令牌，"取消必须一路传出去"这条路径就永远测不出来。
/// </remarks>
internal sealed class RecordingBatchStore : IBatchStore
{
    private readonly List<AggregationBatch> _saved = [];

    /// <summary>每次 SaveAsync 落下的状态快照，按调用顺序排列。</summary>
    public IReadOnlyList<BatchStatus> SavedStatuses => [.. _saved.Select(static batch => batch.Status)];

    public Task SaveAsync(AggregationBatch batch, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _saved.Add(batch);

        return Task.CompletedTask;
    }

    public Task<AggregationBatch?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
        Task.FromResult<AggregationBatch?>(_saved.LastOrDefault(batch => batch.Id == id));

    public Task<IReadOnlyList<AggregationBatch>> GetPendingAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AggregationBatch>>([]);
}

/// <summary>只记不做的摘要存储；可注入写入失败。</summary>
internal sealed class RecordingDigestStore : IDigestStore
{
    private readonly List<Digest> _saved = [];

    /// <summary>非 null 时 <c>SaveAsync</c> 抛这个异常（失败注入）。</summary>
    public Exception? FailWith { get; init; }

    /// <summary>已写入的摘要。</summary>
    public IReadOnlyList<Digest> Saved => _saved;

    public Task SaveAsync(Digest digest, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (FailWith is not null)
        {
            throw FailWith;
        }

        _saved.Add(digest);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Digest>> GetByBatchAsync(string batchId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Digest>>([.. _saved.Where(digest => digest.BatchId == batchId)]);

    public Task<IReadOnlyList<Digest>> GetRecentAsync(int limit, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Digest>>(_saved);

    public Task<IReadOnlyList<Digest>> GetByCategoryAsync(
        DigestCategory category,
        int limit,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Digest>>([.. _saved.Where(digest => digest.Category == category)]);
}

/// <summary>
/// 只记不做的行动项存储；可注入写入失败。
/// </summary>
/// <remarks>
/// 这里刻意<b>不</b>实现状态保护：BatchProcessor 层的"先查后写"已删除，
/// 保护由真实仓储在 SQL 里完成。用一个会照单全收的假实现，才能看清编排层到底发了什么。
/// </remarks>
internal sealed class RecordingTaskStore : ITaskStore
{
    private readonly List<IReadOnlyList<ActionItem>> _batches = [];

    /// <summary>非 null 时 <c>SaveAllAsync</c> / <c>SaveAsync</c> 抛这个异常（失败注入）。</summary>
    public Exception? FailWith { get; init; }

    /// <summary>每次批量调用收到的行动项快照，按调用顺序排列。</summary>
    public IReadOnlyList<IReadOnlyList<ActionItem>> SaveAllCalls => _batches;

    /// <summary>
    /// <see cref="GetByIdAsync"/> 的调用次数。编排层不该再靠"先查后写"保护用户状态，
    /// 这条计数就是那道回归的哨兵。
    /// </summary>
    public int GetByIdCallCount { get; private set; }

    public Task SaveAsync(ActionItem item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (FailWith is not null)
        {
            throw FailWith;
        }

        _batches.Add([item]);
        return Task.CompletedTask;
    }

    public Task SaveAllAsync(IReadOnlyList<ActionItem> items, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (FailWith is not null)
        {
            throw FailWith;
        }

        _batches.Add(items);
        return Task.CompletedTask;
    }

    public Task<ActionItem?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        GetByIdCallCount++;
        return Task.FromResult<ActionItem?>(null);
    }

    public Task<IReadOnlyList<ActionItem>> GetPendingAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ActionItem>>([]);

    public Task SetCompletedAsync(string id, bool completed, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task SetIgnoredAsync(string id, bool ignored, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DeleteAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>返回预置结果的摘要客户端。</summary>
internal sealed class StubSummarizationClient : ISummarizationClient
{
    /// <summary>调用次数。</summary>
    public int CallCount { get; private set; }

    /// <summary>要返回的结果。</summary>
    public LlmResult Result { get; init; } = new() { IsSuccess = true };

    public Task<LlmResult> SummarizeAsync(AggregationBatch batch, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        return Task.FromResult(Result);
    }
}

/// <summary>记录紧急通知调用的通知服务。</summary>
internal sealed class RecordingNotificationService : INotificationService
{
    /// <summary>收到的紧急通知，元素为 (标题, 正文)。</summary>
    public List<(string Title, string Message)> UrgentCalls { get; } = [];

    public void NotifyUrgent(string title, string message) => UrgentCalls.Add((title, message));

    public void NotifyDeadline(string title, string message)
    {
    }
}