using System.Globalization;
using StreamingAggregation.Core;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Diagnostics;
using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Infrastructure.Aggregation;

/// <summary>
/// 批次处理器：把一个到期批次走完「调模型 → 落行动项 → 落摘要 → 标记完成 → 按需紧急通知」。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么放在 Infrastructure 而不是留在 WinUI 侧</b>：这段编排不碰任何 UI 类型，它只是把 Core
/// 抽象接口按顺序串起来。原先它写在 App 程序集的 <c>MessagePipeline</c> 里，而测试宿主加载不了
/// WinUI 程序集，于是"行动项写失败会不会把批次标成完成""摘要写失败会怎样"这类顺序问题只能靠手点验证。
/// 下沉之后测试项目（只引用 Core 与 Infrastructure）可以直接构造 <see cref="BatchProcessor"/> 断言。
/// </para>
/// <para>
/// <b>写入顺序：行动项 → 摘要 → 标记完成</b>。两步都可能失败，所以顺序决定的是"失败时留下哪种孤儿"：
/// 先写摘要会留下"摘要声称有待办、待办列表里却没有对应行"，先写行动项则留下"待办多出来源摘要"。
/// 两者都有窗口，前者更糟——用户在摘要页读到"需要做 X"，翻遍待办列表都找不到，而批次已被标成
/// Completed 不会重来，是静默丢数据；后者只是多出可手动删掉的待办，且批次会标 Failed 留下痕迹。
/// </para>
/// <para>
/// <b>失败语义</b>：模型失败、行动项事务失败、摘要写入失败，三种情况都标 <see cref="BatchStatus.Failed"/>
/// 而不是 <see cref="BatchStatus.Completed"/>。刻意<b>不</b>自动重扫：重扫要再调一次模型，是真金白银的费用，
/// 而且重跑也未必得到同一结果；宁可留下一个可人工核对的 Failed 批次。
/// </para>
/// <para>
/// 行动项写入沿用 <see cref="ITaskStore"/> 的「后台刷新」语义（不覆盖 completed/ignored），
/// 状态保护在 SQL 的 <c>ON CONFLICT DO UPDATE</c> 分支里，编排层不需要先查后写——
/// 那种写法本身就是 TOCTOU：查完到写之间用户勾掉待办，写回时照样被冲掉。
/// </para>
/// </remarks>
public sealed class BatchProcessor
{
    /// <summary>日志里最多点名多少个行动项 ID，超出部分只报总数。</summary>
    private const int MaxLoggedItemIds = 5;

    /// <summary>失败描述截断长度，避免整段模型响应刷屏。</summary>
    private const int MaxFailureLength = 200;

    /// <summary>日志回调缺省时使用的空实现：没有日志出口不该让编排无法构造。</summary>
    private static readonly Action<PipelineLogLevel, string> NoOpLog = static (_, _) => { };

    private readonly IBatchStore _batchStore;
    private readonly IDigestStore _digestStore;
    private readonly ITaskStore _taskStore;
    private readonly ISummarizationClient _summarizer;
    private readonly INotificationService _notifications;
    private readonly Action<PipelineLogLevel, string> _log;

    /// <summary>创建批次处理器。</summary>
    /// <param name="batchStore">批次存储：负责批次的处理中/完成/失败状态流转。</param>
    /// <param name="digestStore">摘要存储。</param>
    /// <param name="taskStore">行动项（待办）存储，批量路径走 <see cref="ITaskStore.SaveAllAsync"/>。</param>
    /// <param name="summarizer">摘要客户端。</param>
    /// <param name="notifications">通知通道，仅在摘要分类为紧急时使用。</param>
    /// <param name="log">日志回调；不传则不记日志。</param>
    public BatchProcessor(
        IBatchStore batchStore,
        IDigestStore digestStore,
        ITaskStore taskStore,
        ISummarizationClient summarizer,
        INotificationService notifications,
        Action<PipelineLogLevel, string>? log = null)
    {
        Guard.NotNull(batchStore);
        Guard.NotNull(digestStore);
        Guard.NotNull(taskStore);
        Guard.NotNull(summarizer);
        Guard.NotNull(notifications);

        _batchStore = batchStore;
        _digestStore = digestStore;
        _taskStore = taskStore;
        _summarizer = summarizer;
        _notifications = notifications;
        _log = log ?? NoOpLog;
    }

    /// <summary>
    /// 处理一个到期批次：标处理中 → 调模型 → 落行动项 → 落摘要 → 标完成 → 按需紧急通知。
    /// </summary>
    /// <remarks>
    /// 除取消外不向外抛异常：单个批次的失败被就地吸收（标 Failed + 记日志），
    /// 一个窗口里可能有多个批次，一个坏批次不该连累同批的其他批次。
    /// 取消则原样抛出——管线正在停，继续写库没有意义。
    /// </remarks>
    /// <param name="batch">到期批次。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task ProcessAsync(AggregationBatch batch, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(batch);

        await _batchStore
            .SaveAsync(batch with { Status = BatchStatus.Processing }, cancellationToken)
            .ConfigureAwait(false);

        var result = await _summarizer.SummarizeAsync(batch, cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess || result.Digest is not { } digest)
        {
            // 先记日志再改状态：状态写入本身也可能失败，日志是那时唯一留下的线索。
            _log(
                PipelineLogLevel.Warning,
                string.Format(
                    CultureInfo.CurrentCulture,
                    "批次 {0}（群 {1}）摘要失败：{2}",
                    batch.Id,
                    batch.GroupId,
                    SummarizeFailure(result.RawResponse)));

            await MarkFailedAsync(batch, cancellationToken).ConfigureAwait(false);
            return;
        }

        await PersistAsync(batch, digest, result.ActionItems, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 落库三步：行动项整批 → 摘要 → 标记完成。任一步失败就标 Failed 并停在这里，绝不标 Completed。
    /// </summary>
    /// <param name="batch">当前批次。</param>
    /// <param name="digest">本轮摘要。</param>
    /// <param name="actionItems">本轮产出的行动项，可为空列表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task PersistAsync(
        AggregationBatch batch,
        Digest digest,
        IReadOnlyList<ActionItem> actionItems,
        CancellationToken cancellationToken)
    {
        if (!await SaveActionItemsAsync(batch, actionItems, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (!await SaveDigestAsync(batch, digest, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await _batchStore
            .SaveAsync(batch with { Status = BatchStatus.Completed }, cancellationToken)
            .ConfigureAwait(false);

        NotifyIfUrgent(digest);
    }

    /// <summary>
    /// 整批写行动项。返回 <see langword="false"/> 表示已标 Failed、调用方应当中止后续步骤。
    /// </summary>
    /// <param name="batch">当前批次。</param>
    /// <param name="actionItems">本轮产出的行动项。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功写入返回 <see langword="true"/>。</returns>
    private async Task<bool> SaveActionItemsAsync(
        AggregationBatch batch,
        IReadOnlyList<ActionItem> actionItems,
        CancellationToken cancellationToken)
    {
        try
        {
            // 一个事务写完整批：要么全进库，要么一条都不进。部分写入在结构上就不可能发生。
            await _taskStore.SaveAllAsync(actionItems, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log(
                PipelineLogLevel.Warning,
                string.Format(
                    CultureInfo.CurrentCulture,
                    "批次 {0}（群 {1}）的 {2} 条行动项整批写入失败，已全部回滚：{3}；未写入的行动项：{4}",
                    batch.Id,
                    batch.GroupId,
                    actionItems.Count,
                    SummarizeFailure(ex.Message),
                    DescribeItemIds(actionItems)));

            await MarkFailedAsync(batch, cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>
    /// 写摘要。返回 <see langword="false"/> 表示已标 Failed、调用方应当中止后续步骤。
    /// </summary>
    /// <param name="batch">当前批次。</param>
    /// <param name="digest">本轮摘要。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入成功返回 <see langword="true"/>。</returns>
    private async Task<bool> SaveDigestAsync(
        AggregationBatch batch,
        Digest digest,
        CancellationToken cancellationToken)
    {
        try
        {
            await _digestStore.SaveAsync(digest, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 行动项已经落库，摘要没写成：批次标 Failed，待办列表里会多出没有摘要出处的条目，
            // 人工核对后删除即可。这类孤儿比"摘要声称有待办却查无此行"轻。
            _log(
                PipelineLogLevel.Warning,
                string.Format(
                    CultureInfo.CurrentCulture,
                    "批次 {0}（群 {1}）的摘要写入失败（行动项已入库，将留下无摘要出处的待办）：{2}",
                    batch.Id,
                    batch.GroupId,
                    SummarizeFailure(ex.Message)));

            await MarkFailedAsync(batch, cancellationToken).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>把批次标成失败；不吞异常，写入失败时由调用方继续上报。</summary>
    /// <param name="batch">当前批次。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task MarkFailedAsync(AggregationBatch batch, CancellationToken cancellationToken)
    {
        await _batchStore
            .SaveAsync(batch with { Status = BatchStatus.Failed }, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>摘要分类为紧急时发一条高优先级通知。</summary>
    /// <param name="digest">摘要。</param>
    private void NotifyIfUrgent(Digest digest)
    {
        if (digest.Category != DigestCategory.Urgent)
        {
            return;
        }

        _notifications.NotifyUrgent(digest.Summary, digest.Reason);
    }

    /// <summary>
    /// 点名未写入的行动项 ID：整批回滚时"到底哪些没进去"没法从异常里读出来，
    /// 只能把本轮的 ID 列出来供人工核对，多了只报总数。
    /// </summary>
    /// <param name="actionItems">本轮产出的行动项。</param>
    /// <returns>逗号分隔的 ID 描述。</returns>
    private static string DescribeItemIds(IReadOnlyList<ActionItem> actionItems)
    {
        if (actionItems.Count == 0)
        {
            return "（无）";
        }

        var listed = actionItems.Take(MaxLoggedItemIds).Select(static item => item.Id);
        var overflow = actionItems.Count > MaxLoggedItemIds ? $" 等共 {actionItems.Count} 条" : string.Empty;

        return string.Join("、", listed) + overflow;
    }

    /// <summary>把失败描述压成一行，避免整段响应刷屏。</summary>
    /// <param name="rawResponse">模型原始响应或异常消息。</param>
    /// <returns>截断后的描述。</returns>
    private static string SummarizeFailure(string? rawResponse)
    {
        if (string.IsNullOrWhiteSpace(rawResponse))
        {
            return "模型未返回可用结果。";
        }

        // net48 的 string.IsNullOrWhiteSpace 没有 [NotNullWhen(false)] 标注（BCL 对可空分析
        // 是"无标注"的），编译器不会自动把 rawResponse 收窄成非空，这里显式收一次。
        var text = rawResponse!;

        // StringSplitOptions.TrimEntries 是 .NET 5+；先按 \r / \n 切、逐段 Trim、再丢掉空段，
        // 与「切分时同时 Trim 并去空」得到的结果完全一致。
        var collapsed = string.Join(
            " ",
            text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(static line => line.Trim())
                .Where(static line => line.Length > 0));

        return collapsed.Length <= MaxFailureLength ? collapsed : collapsed[..MaxFailureLength] + "…";
    }
}