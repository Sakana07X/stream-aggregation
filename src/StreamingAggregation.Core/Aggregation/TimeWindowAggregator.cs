using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Core.Aggregation;

/// <summary>
/// 按 <c>GroupId</c> 分桶的固定长度时间窗口聚合器。
/// <para>
/// 窗口按纪元对齐：窗口起点 = floor(now / 窗口长度) * 窗口长度。
/// 对齐保证同一段时间内的消息在重启前后落到同一个桶，恢复逻辑才成立。
/// </para>
/// <para>
/// 并发安全：所有状态由单个 <c>lock</c> 保护，锁内不做 await。
/// </para>
/// </summary>
public sealed class TimeWindowAggregator : IAggregationWindow
{
    private readonly AggregationOptions _options;
    private readonly TimeSpan _windowDuration;
    private readonly long _windowTicks;
    private readonly IClock _clock;
    private readonly IMessageStore? _messageStore;
    private readonly IReadOnlyDictionary<string, string> _groupNames;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<long, WindowBucket>> _buckets = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GroupCounters> _counters = new(StringComparer.Ordinal);
    private readonly List<ReadyBatch> _ready = [];
    private long _sequence;

    /// <summary>
    /// 创建聚合窗口引擎。
    /// </summary>
    /// <param name="options">窗口配置，不能为 null。</param>
    /// <param name="clock">时间源，不能为 null。</param>
    /// <param name="messageStore">
    /// 消息存储。仅 <see cref="RestoreAsync"/> 需要它，用于重启恢复；不传则无法恢复。
    /// </param>
    /// <param name="groupNames">可选的群标识到群名称映射，用于填充批次的 <c>GroupName</c>；缺省回落为群标识。</param>
    public TimeWindowAggregator(
        AggregationOptions options,
        IClock clock,
        IMessageStore? messageStore = null,
        IReadOnlyDictionary<string, string>? groupNames = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        _options = options;
        _windowDuration = options.WindowDuration;
        _windowTicks = _windowDuration.Ticks;
        _clock = clock;
        _messageStore = messageStore;
        _groupNames = groupNames ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public Task AddMessageAsync(Message message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.MessageId, nameof(message.MessageId));
        ArgumentException.ThrowIfNullOrWhiteSpace(message.GroupId, nameof(message.GroupId));

        var windowStart = GetWindowStart(_clock.Now);
        lock (_gate)
        {
            var counters = GetCounters(message.GroupId);
            var bucket = GetOrCreateBucket(message.GroupId, windowStart);
            if (!bucket.TryAdd(message))
            {
                counters.Duplicates++;
                return Task.CompletedTask;
            }

            counters.TotalMessages++;
            if (bucket.Count >= _options.MaxMessagesPerWindow)
            {
                counters.EarlyFlushes++;
                _ready.Add(Finalize(bucket));
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<AggregationBatch>> FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = _clock.Now;
        ReadyBatch[] collected;
        lock (_gate)
        {
            // 先取走此前因触顶而提前成批的批次，再补上本次新到点的窗口。
            var result = new List<ReadyBatch>(_ready);
            _ready.Clear();

            var due = new List<WindowBucket>();
            foreach (var group in _buckets.Values)
            {
                foreach (var bucket in group.Values)
                {
                    if (bucket.WindowEnd <= now)
                    {
                        due.Add(bucket);
                    }
                }
            }

            foreach (var bucket in due)
            {
                result.Add(Finalize(bucket));
            }

            result.Sort(CompareReady);
            collected = result.ToArray();
        }

        var batches = new AggregationBatch[collected.Length];
        for (int i = 0; i < collected.Length; i++)
        {
            batches[i] = collected[i].Batch;
        }

        return Task.FromResult<IReadOnlyList<AggregationBatch>>(batches);
    }

    /// <summary>批次排序：窗口起点 -> 群标识 -> 成批顺序，保证输出稳定可断言。</summary>
    private static int CompareReady(ReadyBatch left, ReadyBatch right)
    {
        int byWindow = left.Batch.WindowStart.CompareTo(right.Batch.WindowStart);
        if (byWindow != 0)
        {
            return byWindow;
        }

        int byGroup = string.CompareOrdinal(left.Batch.GroupId, right.Batch.GroupId);
        return byGroup != 0 ? byGroup : left.Sequence.CompareTo(right.Sequence);
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, AggregationWindowStats> GetStats()
    {
        lock (_gate)
        {
            var stats = new Dictionary<string, AggregationWindowStats>(_buckets.Count + _counters.Count, StringComparer.Ordinal);

            foreach (var (groupId, group) in _buckets)
            {
                stats[groupId] = BuildStats(groupId, group, _counters.GetValueOrDefault(groupId));
            }

            // 已清空全部窗口但本进程产出过批次的群组也要可见，便于界面展示历史产出。
            foreach (var (groupId, counters) in _counters)
            {
                if (!stats.ContainsKey(groupId))
                {
                    stats[groupId] = BuildStats(groupId, null, counters);
                }
            }

            return stats;
        }
    }

    /// <inheritdoc />
    public async Task<int> RestoreAsync(
        IEnumerable<string> groupIds,
        DateTime resumeAfter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupIds);
        var messageStore = _messageStore
            ?? throw new InvalidOperationException("未注入 IMessageStore，无法从存储重建未关闭窗口。");

        var now = _clock.Now;
        var resume = ToUtc(resumeAfter);
        var restored = 0;

        foreach (var groupId in groupIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(groupId))
            {
                continue;
            }

            restored += await RestoreGroupAsync(messageStore, groupId, resume, now, cancellationToken).ConfigureAwait(false);
        }

        return restored;
    }

    private async Task<int> RestoreGroupAsync(
        IMessageStore messageStore,
        string groupId,
        DateTime resume,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var messages = await messageStore.GetByGroupAsync(groupId, resume, cancellationToken).ConfigureAwait(false);
        if (messages is null || messages.Count == 0)
        {
            return 0;
        }

        // 按时间戳升序重建，保证跨窗口归属正确；顺带兜住未正确应用 since 过滤的存储实现。
        // 排序键必须与过滤条件同用 ToUtc 归一：裸值排序会在混合 Kind 时按墙钟时刻错排，
        // 与后面按 UTC 归属窗口的顺序不一致，导致消息落错桶。
        var ordered = messages
            .Where(message => message is not null
                && string.Equals(message.GroupId, groupId, StringComparison.Ordinal)
                && ToUtc(message.Timestamp) >= resume)
            .OrderBy(message => ToUtc(message.Timestamp), Comparer<DateTime>.Default)
            .ToArray();

        var restored = 0;
        lock (_gate)
        {
            foreach (var message in ordered)
            {
                // 未来时间戳（对端时钟超前）收敛到当前时刻，避免开出尚未到来的窗口。
                var instant = ToUtc(message.Timestamp);
                if (instant > now)
                {
                    instant = now;
                }

                var counters = GetCounters(message.GroupId);
                var bucket = GetOrCreateBucket(message.GroupId, GetWindowStart(instant));
                if (!bucket.TryAdd(message))
                {
                    counters.Duplicates++;
                    continue;
                }

                counters.TotalMessages++;
                restored++;

                if (bucket.Count >= _options.MaxMessagesPerWindow)
                {
                    counters.EarlyFlushes++;
                    _ready.Add(Finalize(bucket));
                }
            }
        }

        return restored;
    }

    private GroupCounters GetCounters(string groupId)
    {
        if (!_counters.TryGetValue(groupId, out var counters))
        {
            counters = new GroupCounters();
            _counters[groupId] = counters;
        }

        return counters;
    }

    private WindowBucket GetOrCreateBucket(string groupId, DateTime windowStart)
    {
        if (!_buckets.TryGetValue(groupId, out var group))
        {
            group = new Dictionary<long, WindowBucket>();
            _buckets[groupId] = group;
        }

        if (!group.TryGetValue(windowStart.Ticks, out var bucket))
        {
            bucket = new WindowBucket(groupId, ResolveGroupName(groupId), windowStart, windowStart + _windowDuration);
            group[windowStart.Ticks] = bucket;
        }

        return bucket;
    }

    /// <summary>
    /// 把桶转成批次并移出内存桶集合：成批即回收，空桶随之消失。
    /// 返回的批次由调用方决定是直接交给 <see cref="FlushAsync"/> 的调用方，
    /// 还是先进入 <c>_ready</c> 交接队列。
    /// 调用方必须持有 <c>_gate</c>。
    /// </summary>
    private ReadyBatch Finalize(WindowBucket bucket)
    {
        var sequence = ++_sequence;
        var ready = new ReadyBatch(sequence, bucket.ToBatch($"batch-{Guid.NewGuid():N}"));

        if (_buckets.TryGetValue(bucket.GroupId, out var group))
        {
            group.Remove(bucket.WindowStart.Ticks);
            if (group.Count == 0)
            {
                _buckets.Remove(bucket.GroupId);
            }
        }

        if (_counters.TryGetValue(bucket.GroupId, out var counters))
        {
            counters.FlushedWindows++;
        }

        return ready;
    }

    private AggregationWindowStats BuildStats(string groupId, Dictionary<long, WindowBucket>? group, GroupCounters? counters)
    {
        var newest = group is null || group.Count == 0 ? null : group.Values.MaxBy(bucket => bucket.WindowStart);
        return new AggregationWindowStats
        {
            GroupId = groupId,
            GroupName = ResolveGroupName(groupId),
            WindowStart = newest?.WindowStart ?? default,
            WindowEnd = newest?.WindowEnd ?? default,
            MessageCount = newest?.Count ?? 0,
            PendingMessageCount = group is null ? 0 : group.Values.Sum(bucket => bucket.Count),
            OpenWindowCount = group?.Count ?? 0,
            TotalMessageCount = counters?.TotalMessages ?? 0,
            FlushedWindowCount = counters?.FlushedWindows ?? 0,
            EarlyFlushCount = counters?.EarlyFlushes ?? 0,
            DuplicateCount = counters?.Duplicates ?? 0,
            MaxMessagesPerWindow = _options.MaxMessagesPerWindow,
        };
    }

    private string ResolveGroupName(string groupId)
        => _groupNames.TryGetValue(groupId, out var name) && !string.IsNullOrWhiteSpace(name) ? name : groupId;

    /// <summary>取得 <paramref name="instant"/> 所处的对齐窗口起点。</summary>
    private DateTime GetWindowStart(DateTime instant)
    {
        var ticks = ToUtc(instant).Ticks;
        return new DateTime((ticks / _windowTicks) * _windowTicks, DateTimeKind.Utc);
    }

    /// <summary>统一到 UTC：本地时间换算，无 Kind 的按 UTC 解释。</summary>
    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>一个群组在一个时间窗口内的待聚合消息。</summary>
    private sealed class WindowBucket
    {
        private readonly HashSet<string> _messageIds = new(StringComparer.Ordinal);

        public WindowBucket(string groupId, string groupName, DateTime windowStart, DateTime windowEnd)
        {
            GroupId = groupId;
            GroupName = groupName;
            WindowStart = windowStart;
            WindowEnd = windowEnd;
        }

        public string GroupId { get; }

        public string GroupName { get; }

        public DateTime WindowStart { get; }

        public DateTime WindowEnd { get; }

        public List<Message> Messages { get; } = [];

        public int Count => Messages.Count;

        /// <summary>入桶；同一窗口内 <c>MessageId</c> 重复时返回 false。</summary>
        public bool TryAdd(Message message)
        {
            if (!_messageIds.Add(message.MessageId))
            {
                return false;
            }

            Messages.Add(message);
            return true;
        }

        /// <summary>导出批次快照，消息列表与桶内列表脱离引用关系。</summary>
        public AggregationBatch ToBatch(string id) => new()
        {
            Id = id,
            GroupId = GroupId,
            GroupName = GroupName,
            Messages = Messages.ToArray(),
            WindowStart = WindowStart,
            WindowEnd = WindowEnd,
            Status = BatchStatus.Pending,
        };
    }

    /// <summary>群组维度的累计计数。</summary>
    private sealed class GroupCounters
    {
        public long TotalMessages { get; set; }

        public long FlushedWindows { get; set; }

        public long EarlyFlushes { get; set; }

        public long Duplicates { get; set; }
    }

    /// <summary>已成批但尚未被 <see cref="FlushAsync"/> 取走的批次。</summary>
    private sealed record ReadyBatch(long Sequence, AggregationBatch Batch);
}