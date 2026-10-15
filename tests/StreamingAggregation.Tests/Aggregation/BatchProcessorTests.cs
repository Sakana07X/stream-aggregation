using StreamingAggregation.Core;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Models;
using StreamingAggregation.Infrastructure;
using StreamingAggregation.Infrastructure.Aggregation;
using StreamingAggregation.Tests.Fakes;

namespace StreamingAggregation.Tests.Aggregation;

/// <summary>
/// <see cref="BatchProcessor"/> 的编排行为。
/// </summary>
/// <remarks>
/// 这组用例是编排层从 WinUI 侧下沉到 Infrastructure 换来的：原先这段逻辑在测试宿主加载不了的
/// 程序集里，"摘要失败会不会标完成""行动项写一半怎么办"只能靠手点验证。
/// </remarks>
public sealed class BatchProcessorTests
{
    private const string BatchId = "test-batch-0001";
    private const string GroupId = "test-group-id";

    private static readonly DateTime BaseTime = new(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ProcessAsync_MarksProcessingThenWritesItemsAndDigestThenCompleted()
    {
        var doubles = new Doubles();

        await CreateProcessor(doubles).ProcessAsync(BuildBatch());

        // 顺序本身就是需求：行动项先于摘要，Completed 最后。
        // 反过来写的话，摘要失败就会留下"摘要声称有待办、待办列表查无此行"的静默缺失。
        Assert.Equal([BatchStatus.Processing, BatchStatus.Completed], doubles.Batches.SavedStatuses);
        Assert.Single(doubles.Digests.Saved);
    }

    [Fact]
    public async Task ProcessAsync_WritesAllActionItemsInOneBatchCall()
    {
        var doubles = new Doubles();

        await CreateProcessor(doubles).ProcessAsync(BuildBatch());

        // 一次调用、整批行动项：逐条写就等于把"部分成功"的可能性重新引入编排层。
        Assert.Single(doubles.Tasks.SaveAllCalls);
        Assert.Equal(
            ["test-action-1", "test-action-2"],
            doubles.Tasks.SaveAllCalls[0].Select(static item => item.Id));
    }

    [Fact]
    public async Task ProcessAsync_ModelFailure_MarksFailedAndWritesNothing()
    {
        var doubles = new Doubles
        {
            Summarizer = new StubSummarizationClient
            {
                Result = new LlmResult { IsSuccess = false, RawResponse = "模型返回了乱码" },
            },
        };

        await CreateProcessor(doubles).ProcessAsync(BuildBatch());

        Assert.Equal([BatchStatus.Processing, BatchStatus.Failed], doubles.Batches.SavedStatuses);
        Assert.Empty(doubles.Tasks.SaveAllCalls);
        Assert.Empty(doubles.Digests.Saved);
        Assert.Empty(doubles.Notifications.UrgentCalls);
    }

    [Fact]
    public async Task ProcessAsync_ActionItemWriteFailure_MarksFailedAndNeverCompletes()
    {
        // 交接文档 P1-3 的核心：摘要成功、行动项写不进去时，绝不能标 Completed。
        var doubles = new Doubles
        {
            Tasks = new RecordingTaskStore { FailWith = new InvalidOperationException("写入失败") },
        };

        await CreateProcessor(doubles).ProcessAsync(BuildBatch());

        Assert.Equal([BatchStatus.Processing, BatchStatus.Failed], doubles.Batches.SavedStatuses);
        Assert.DoesNotContain(BatchStatus.Completed, doubles.Batches.SavedStatuses);
        // 行动项写失败时摘要也不该写：没有摘要出处的行越多，人工核对的成本越高。
        Assert.Empty(doubles.Digests.Saved);
        Assert.Empty(doubles.Notifications.UrgentCalls);
    }

    [Fact]
    public async Task ProcessAsync_ActionItemWriteFailure_LogsWhichItemsWereLost()
    {
        var logged = new List<(PipelineLogLevel Level, string Message)>();
        var doubles = new Doubles
        {
            Tasks = new RecordingTaskStore { FailWith = new InvalidOperationException("写入失败") },
        };

        var processor = new BatchProcessor(
            doubles.Batches,
            doubles.Digests,
            doubles.Tasks,
            doubles.Summarizer,
            doubles.Notifications,
            (level, message) => logged.Add((level, message)));

        await processor.ProcessAsync(BuildBatch());

        var warning = Assert.Single(logged, static entry => entry.Level == PipelineLogLevel.Warning);

        // 日志必须能回答"到底哪些待办没进去"，否则用户只能靠猜。
        Assert.Contains("test-action-1", warning.Message, StringComparison.Ordinal);
        Assert.Contains("test-action-2", warning.Message, StringComparison.Ordinal);
        Assert.Contains("写入失败", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessAsync_DigestWriteFailure_MarksFailedAndKeepsItemsAlreadyWritten()
    {
        var doubles = new Doubles
        {
            Digests = new RecordingDigestStore { FailWith = new InvalidOperationException("磁盘只读") },
        };

        await CreateProcessor(doubles).ProcessAsync(BuildBatch());

        // 这是有意选择的失败窗口方向：多出没有摘要出处的待办，比"摘要有、待办无"轻——
        // 前者可人工删除，后者在批次被标完成后无法察觉。
        Assert.Equal([BatchStatus.Processing, BatchStatus.Failed], doubles.Batches.SavedStatuses);
        Assert.Single(doubles.Tasks.SaveAllCalls);
        Assert.Empty(doubles.Notifications.UrgentCalls);
    }

    [Fact]
    public async Task ProcessAsync_UrgentDigest_NotifiesAfterCompletion()
    {
        var doubles = new Doubles
        {
            Summarizer = new StubSummarizationClient { Result = BuildResult(DigestCategory.Urgent) },
        };

        await CreateProcessor(doubles).ProcessAsync(BuildBatch());

        var (title, message) = Assert.Single(doubles.Notifications.UrgentCalls);
        Assert.Equal("test-summary", title);
        Assert.Equal("test-reason", message);
        Assert.Equal(BatchStatus.Completed, doubles.Batches.SavedStatuses[^1]);
    }

    [Fact]
    public async Task ProcessAsync_NonUrgentDigest_DoesNotNotify()
    {
        var doubles = new Doubles();

        await CreateProcessor(doubles).ProcessAsync(BuildBatch());

        Assert.Empty(doubles.Notifications.UrgentCalls);
    }

    [Fact]
    public async Task ProcessAsync_Cancellation_PropagatesAndStopsProcessing()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var doubles = new Doubles();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateProcessor(doubles).ProcessAsync(BuildBatch(), cancellation.Token));

        // 管线正在停，不该继续写库。
        Assert.Empty(doubles.Tasks.SaveAllCalls);
        Assert.Empty(doubles.Digests.Saved);
    }

    [Fact]
    public async Task ProcessAsync_DoesNotQueryTaskStoreBeforeWriting()
    {
        // 编排层不该再"先查后写"：状态保护在 SQL 的 UPSERT 冲突分支里。
        // 一旦有人把 GetByIdAsync 加回编排路径（"先查后写"正是 TOCTOU），这里立刻红。
        var doubles = new Doubles();

        await CreateProcessor(doubles).ProcessAsync(BuildBatch());

        Assert.Equal(0, doubles.Tasks.GetByIdCallCount);
    }

    [Fact]
    public async Task ProcessAsync_WithoutLogCallback_StillCompletes()
    {
        // 日志是可选依赖：没有日志出口时编排照常工作，不能因为排障设施缺失就停摆。
        var doubles = new Doubles();
        var processor = new BatchProcessor(
            doubles.Batches,
            doubles.Digests,
            doubles.Tasks,
            doubles.Summarizer,
            doubles.Notifications,
            log: null);

        await processor.ProcessAsync(BuildBatch());

        Assert.Equal([BatchStatus.Processing, BatchStatus.Completed], doubles.Batches.SavedStatuses);
    }

    [Fact]
    public async Task ProcessAsync_RejectsNullBatch()
    {
        var doubles = new Doubles();

        await Assert.ThrowsAsync<ArgumentNullException>(() => CreateProcessor(doubles).ProcessAsync(null!));
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        var doubles = new Doubles();

        Assert.Throws<ArgumentNullException>(() => new BatchProcessor(null!, doubles.Digests, doubles.Tasks, doubles.Summarizer, doubles.Notifications));
        Assert.Throws<ArgumentNullException>(() => new BatchProcessor(doubles.Batches, null!, doubles.Tasks, doubles.Summarizer, doubles.Notifications));
        Assert.Throws<ArgumentNullException>(() => new BatchProcessor(doubles.Batches, doubles.Digests, null!, doubles.Summarizer, doubles.Notifications));
        Assert.Throws<ArgumentNullException>(() => new BatchProcessor(doubles.Batches, doubles.Digests, doubles.Tasks, null!, doubles.Notifications));
        Assert.Throws<ArgumentNullException>(() => new BatchProcessor(doubles.Batches, doubles.Digests, doubles.Tasks, doubles.Summarizer, null!));
    }

    // ---------------------------------------------------------------------
    // 夹具与辅助
    // ---------------------------------------------------------------------

    private static BatchProcessor CreateProcessor(Doubles doubles) =>
        new(doubles.Batches, doubles.Digests, doubles.Tasks, doubles.Summarizer, doubles.Notifications);

    private static AggregationBatch BuildBatch() => new()
    {
        Id = BatchId,
        GroupId = GroupId,
        GroupName = "测试群",
        WindowStart = BaseTime,
        WindowEnd = BaseTime.AddMinutes(3),
        Status = BatchStatus.Pending,
        Messages = [],
    };

    private static LlmResult BuildResult(DigestCategory category = DigestCategory.Normal) => new()
    {
        IsSuccess = true,
        Digest = new Digest
        {
            Id = "test-digest-0001",
            BatchId = BatchId,
            Category = category,
            Summary = "test-summary",
            Reason = "test-reason",
            CreatedAt = BaseTime,
        },
        ActionItems =
        [
            new ActionItem
            {
                Id = "test-action-1",
                Action = "test-action-1-desc",
                Importance = Importance.High,
                Reason = "test-reason",
                SourceGroupId = GroupId,
            },
            new ActionItem
            {
                Id = "test-action-2",
                Action = "test-action-2-desc",
                Importance = Importance.Low,
                Reason = "test-reason",
                SourceGroupId = GroupId,
            },
        ],
    };

    /// <summary>一整套编排测试替身；每个字段都可替换，用来注入某一步的失败。</summary>
    private sealed class Doubles
    {
        public RecordingBatchStore Batches { get; init; } = new();

        public RecordingDigestStore Digests { get; init; } = new();

        public RecordingTaskStore Tasks { get; init; } = new();

        public StubSummarizationClient Summarizer { get; init; } = new() { Result = BuildResult() };

        public RecordingNotificationService Notifications { get; init; } = new();
    }
}