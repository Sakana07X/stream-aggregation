using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Core.Aggregation;

/// <summary>
/// 聚合窗口：把持续到达的消息按群组分桶、按固定时间窗口成批。
/// <para>
/// 实现必须满足：到点 flush 成 <see cref="AggregationBatch"/>；空桶自动清理；
/// 单窗口消息数有上限；进程重启后可从存储重建未关闭窗口；所有成员并发安全。
/// 所有时间判断都由 <see cref="Abstractions.IClock"/> 驱动，便于测试替换。
/// </para>
/// </summary>
public interface IAggregationWindow
{
    /// <summary>
    /// 把一条消息放入其所属群组、当前所处时间窗口的桶中。
    /// <para>
    /// 消息按“到达时刻”归档到对齐后的固定窗口。若该窗口已达到
    /// <see cref="AggregationOptions.MaxMessagesPerWindow"/>，会立即提前成批，
    /// 该批次在后续 <see cref="FlushAsync"/> 中返回。
    /// </para>
    /// </summary>
    /// <param name="message">入窗消息，<c>MessageId</c> 与 <c>GroupId</c> 不可为空。</param>
    /// <param name="cancellationToken">取消令牌；已取消时抛出 <see cref="OperationCanceledException"/>。</param>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> 为 null。</exception>
    /// <exception cref="ArgumentException">消息缺少 <c>MessageId</c> 或 <c>GroupId</c>。</exception>
    Task AddMessageAsync(Message message, CancellationToken cancellationToken = default);

    /// <summary>
    /// 成批并返回所有“已到窗口结束时间”的批次，同时取走此前因触顶而提前成批的批次。
    /// <para>
    /// 到点的桶在返回后即被移除（空桶自动清理）。同一群组可能因停机或长时间空窗
    /// 一次返回多个批次，每个批次保留自己真实的窗口边界。
    /// </para>
    /// </summary>
    /// <param name="cancellationToken">取消令牌；已取消时抛出 <see cref="OperationCanceledException"/>。</param>
    /// <returns>按窗口开始时间、群组标识排序的批次快照；无到期批次时返回空列表。</returns>
    Task<IReadOnlyList<AggregationBatch>> FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取按群组标识索引的窗口统计快照。
    /// <para>
    /// 返回条目包含当前仍有未关闭窗口的群组，以及本进程生命周期内产出过批次的群组。
    /// </para>
    /// </summary>
    IReadOnlyDictionary<string, AggregationWindowStats> GetStats();

    /// <summary>
    /// 从消息存储重建指定群组尚未成批的窗口，用于进程重启后的恢复。
    /// <para>
    /// 按消息自身时间戳归档到对齐窗口，并把未来时间戳收敛到当前时刻；
    /// <c>MessageId</c> 重复的消息不会重复计入。
    /// </para>
    /// </summary>
    /// <param name="groupIds">需要恢复的群组标识集合（例如群白名单）。</param>
    /// <param name="resumeAfter">
    /// 恢复起点（含）。应取该群最近一个已落库批次的 <see cref="AggregationBatch.WindowEnd"/>，
    /// 以免已摘要过的消息被二次聚合。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际重建进窗口的消息条数。</returns>
    /// <exception cref="InvalidOperationException">实现未注入 <see cref="Abstractions.IMessageStore"/>。</exception>
    Task<int> RestoreAsync(
        IEnumerable<string> groupIds,
        DateTime resumeAfter,
        CancellationToken cancellationToken = default);
}