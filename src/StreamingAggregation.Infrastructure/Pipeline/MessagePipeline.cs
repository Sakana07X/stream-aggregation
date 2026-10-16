using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Aggregation;
using StreamingAggregation.Core.Filtering;
using StreamingAggregation.Core.Models;
using StreamingAggregation.Infrastructure.Aggregation;
using StreamingAggregation.Infrastructure.OneBot;

namespace StreamingAggregation.Infrastructure.Pipeline;

/// <summary>
/// 消息管线：把 OneBot 事件流转成"消息落库 → 进聚合窗口 → 到点摘要 → 待办入库 → 紧急通知"。
/// </summary>
/// <remarks>
/// <para>
/// <b>两个并发循环</b>：<see cref="ConsumeLoopAsync"/> 独占订阅 <see cref="IOneBotEventSource"/>
/// 的事件流（该流同一时刻只允许一个消费者），<see cref="FlushLoopAsync"/> 负责按节奏催熟窗口。
/// 两者都只依赖构造函数注入的服务，本身不创建任何连接或计时器资源之外的依赖。
/// </para>
/// <para>
/// <b>失败隔离</b>：单条消息处理失败只丢这一条并记账，整条管线不因此中断——后台聚合是
/// 锦上添花功能，不能因为一条脏消息或一次模型超时把消息接收一起带崩。
/// </para>
/// <para>
/// <b>批次处理已外移</b>：单批次的「调模型 → 落行动项 → 落摘要 → 标记完成」编排放在
/// Infrastructure 的 <see cref="BatchProcessor"/>，那里的类型不依赖 WinUI，测试项目才能加载并断言。
/// 本类只负责"什么时候该处理哪个批次"，不重复实现编排。
/// </para>
/// <para>
/// <b>为什么放在 Infrastructure 而不是留在 WinUI 侧</b>：本类只依赖 Core 抽象与 Infrastructure 的
/// <see cref="BatchProcessor"/>、<see cref="GroupWhitelistMatcher"/> 等类型，不碰任何 UI 类型。
/// 原先它写在 App 程序集里，而测试宿主加载不了 WinUI 程序集，于是"暂停时消息会不会落库"
/// "催熟间隔怎么算"这类纯逻辑只能靠手点验证。下沉之后测试项目可以直接构造并断言。
/// </para>
/// <para>
/// 管线自身不创建 <see cref="IDisposable"/> 资源（连接、<see cref="IDisposable"/> 服务都由
/// 组合根持有与释放），因此没有 <c>Dispose</c>；停止靠取消 <see cref="CancellationToken"/>。
/// </para>
/// </remarks>
public sealed class MessagePipeline
{
    /// <summary>催熟间隔的最小值：窗口再短也不必比这更频繁地扫。</summary>
    private static readonly TimeSpan MinimumFlushInterval = TimeSpan.FromSeconds(5);

    /// <summary>催熟间隔的默认值（窗口长到没有上限意义时使用）。</summary>
    private static readonly TimeSpan MaximumFlushInterval = TimeSpan.FromMinutes(5);

    private readonly IOneBotEventSource _source;
    private readonly IMessageStore _messageStore;
    private readonly BatchProcessor _batchProcessor;
    private readonly IAggregationWindow _window;
    private readonly Func<GroupWhitelistMatcher> _matcherFactory;
    private readonly IReadOnlyList<IMessageFilter> _filters;
    private readonly Func<bool> _isPaused;
    private readonly Action<PipelineLogLevel, string> _log;
    private readonly TimeSpan _windowDuration;

    /// <summary>
    /// 创建消息管线。
    /// </summary>
    /// <param name="source">OneBot 事件源（独占订阅）。</param>
    /// <param name="messageStore">消息存储。</param>
    /// <param name="batchProcessor">批次处理器；单批次的摘要、落库与通知由它负责。</param>
    /// <param name="window">聚合窗口。</param>
    /// <param name="matcherFactory">按当前设置生成群白名单匹配器的委托。</param>
    /// <param name="filters">按顺序执行的过滤器。</param>
    /// <param name="isPaused">是否处于暂停状态的判定委托。</param>
    /// <param name="log">日志回调。</param>
    /// <param name="windowDuration">当前聚合窗口长度，用于推算催熟节奏。</param>
    public MessagePipeline(
        IOneBotEventSource source,
        IMessageStore messageStore,
        BatchProcessor batchProcessor,
        IAggregationWindow window,
        Func<GroupWhitelistMatcher> matcherFactory,
        IReadOnlyList<IMessageFilter> filters,
        Func<bool> isPaused,
        Action<PipelineLogLevel, string> log,
        TimeSpan windowDuration)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _messageStore = messageStore ?? throw new ArgumentNullException(nameof(messageStore));
        _batchProcessor = batchProcessor ?? throw new ArgumentNullException(nameof(batchProcessor));
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _matcherFactory = matcherFactory ?? throw new ArgumentNullException(nameof(matcherFactory));
        _filters = filters ?? throw new ArgumentNullException(nameof(filters));
        _isPaused = isPaused ?? throw new ArgumentNullException(nameof(isPaused));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _windowDuration = windowDuration > TimeSpan.Zero ? windowDuration : TimeSpan.FromMinutes(3);
    }

    /// <summary>
    /// 启动管线并阻塞到取消。
    /// </summary>
    /// <param name="cancellationToken">取消令牌；取消后两个循环都干净退出。</param>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var consume = ConsumeLoopAsync(cancellationToken);
        var flush = FlushLoopAsync(cancellationToken);

        await Task.WhenAll(consume, flush).ConfigureAwait(false);
    }

    /// <summary>订阅事件流，把每条白名单内的消息落库并投进聚合窗口。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task ConsumeLoopAsync(CancellationToken cancellationToken)
    {
        // 白名单在设置变更后会重建管线，因此这里取一次快照即可。
        var matcher = _matcherFactory();

        try
        {
            await foreach (var received in _source.StreamAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await HandleAsync(received, matcher, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 一条脏消息不该让整条接收循环退出：记下失败继续收。
                    _log(PipelineLogLevel.Warning, $"处理群 {received.GroupId} 的一条消息失败：{ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _log(PipelineLogLevel.Info, "消息管线已停止接收（收到取消信号）。");
        }
        catch (Exception ex)
        {
            // 事件流本身也可能抛协议级错误。记下来，但不向上冒泡把宿主带崩。
            _log(PipelineLogLevel.Error, $"消息接收循环异常退出：{ex.Message}");
        }
    }

    /// <summary>处理单条事件：暂停与白名单过滤 → 规则过滤 → 落库 → 进窗口。</summary>
    /// <param name="received">群消息事件。</param>
    /// <param name="matcher">群白名单匹配器快照。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task HandleAsync(
        GroupMessageEvent received,
        GroupWhitelistMatcher matcher,
        CancellationToken cancellationToken)
    {
        if (_isPaused() || !matcher.Matches(received.GroupId))
        {
            return;
        }

        var message = Convert(received);
        if (!Accept(message))
        {
            return;
        }

        await _messageStore.SaveAsync(message, cancellationToken).ConfigureAwait(false);
        await _window.AddMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按固定节奏催熟聚合窗口。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(ComputeFlushInterval());

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_isPaused())
                {
                    continue;
                }

                await FlushOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止路径。
        }
        catch (Exception ex)
        {
            _log(PipelineLogLevel.Error, $"窗口催熟循环异常退出：{ex.Message}");
        }
    }

    /// <summary>取一次到期批次并逐个交给 <see cref="BatchProcessor"/> 处理。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task FlushOnceAsync(CancellationToken cancellationToken)
    {
        var batches = await _window.FlushAsync(cancellationToken).ConfigureAwait(false);

        foreach (var batch in batches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await _batchProcessor.ProcessAsync(batch, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // BatchProcessor 已就地吸收单批次失败并标 Failed；能冒到这里的是它自己都没兜住的
                // 意外（例如标 Failed 时写库也失败），继续处理同批的下一个批次。
                _log(
                    PipelineLogLevel.Error,
                    $"批次 {batch.Id}（群 {batch.GroupId}）处理失败：{ex.Message}");
            }
        }
    }

    /// <summary>逐个跑过滤器，全部放行才算接受。</summary>
    /// <param name="message">候选消息。</param>
    /// <returns>被接受返回 <see langword="true"/>。</returns>
    private bool Accept(Message message)
    {
        var candidate = new[] { message };

        foreach (var filter in _filters)
        {
            if (filter.Filter(candidate).Count == 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>OneBot 事件 → 领域消息。</summary>
    /// <param name="received">事件。</param>
    /// <returns>消息。</returns>
    private static Message Convert(GroupMessageEvent received) => new()
    {
        MessageId = received.MessageId,
        GroupId = received.GroupId,
        Sender = received.SenderId,
        Text = received.Content,
        Timestamp = received.Timestamp,
    };

    /// <summary>
    /// 催熟间隔取窗口长度的一半，保证窗口边界不会长时间滞留在内存里，
    /// 同时把最坏延迟压在半个窗口之内。
    /// </summary>
    /// <returns>催熟间隔。</returns>
    private TimeSpan ComputeFlushInterval()
    {
        var half = TimeSpan.FromTicks(_windowDuration.Ticks / 2);

        if (half < MinimumFlushInterval)
        {
            return MinimumFlushInterval;
        }

        return half > MaximumFlushInterval ? MaximumFlushInterval : half;
    }
}
