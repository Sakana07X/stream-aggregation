using StreamingAggregation.Core;
using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Core.Abstractions;

public interface IDigestStore
{
    Task SaveAsync(Digest digest, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Digest>> GetByBatchAsync(string batchId, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取最近保存的摘要，按创建时间降序（新的在前）。跨全部批次，供摘要页展示最近列表。
    /// </summary>
    /// <param name="limit">最多返回多少条；超出 1–1000 的取值会被钳制到该区间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<IReadOnlyList<Digest>> GetRecentAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取某个分类下最近保存的摘要，按创建时间降序（新的在前），供摘要页按分类分组展示。
    /// </summary>
    /// <param name="category">目标分类。</param>
    /// <param name="limit">最多返回多少条；超出 1–1000 的取值会被钳制到该区间。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="category"/> 不是已定义的枚举值。</exception>
    Task<IReadOnlyList<Digest>> GetByCategoryAsync(
        DigestCategory category,
        int limit,
        CancellationToken cancellationToken = default);
}
