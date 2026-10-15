using StreamingAggregation.Core;
using StreamingAggregation.Core.Models;
using StreamingAggregation.Infrastructure.Persistence;

namespace StreamingAggregation.Tests.Persistence;

/// <summary>
/// <c>DigestRepository</c> 的列表查询（"最近 N 条"与按分类过滤）。
/// </summary>
/// <remarks>
/// 摘要表对 <c>aggregation_batches</c> 有外键，所以每个用例都要先落一个批次。
/// </remarks>
public sealed class DigestRepositoryTests
{
    private static readonly DateTime BaseTime = new(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetRecentAsync_ReturnsNewestFirstAndHonorsLimit()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var batches = database.CreateBatchRepository();
        var digests = database.CreateDigestRepository();

        var batch = await SaveBatchAsync(batches, "batch-1");
        for (var index = 0; index < 3; index++)
        {
            await digests.SaveAsync(BuildDigest($"digest-{index}", batch.Id, index));
        }

        var recent = await digests.GetRecentAsync(limit: 2);

        Assert.Equal(["digest-2", "digest-1"], recent.Select(static digest => digest.Id));
    }

    [Fact]
    public async Task GetRecentAsync_OrdersByIdWhenTimestampsAreEqual()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var batches = database.CreateBatchRepository();
        var digests = database.CreateDigestRepository();

        var batch = await SaveBatchAsync(batches, "batch-1");
        foreach (var id in new[] { "digest-a", "digest-b", "digest-c" })
        {
            // 同一时间戳：只按 created_at 排时行序不确定，次级键必须兜住。
            await digests.SaveAsync(BuildDigest(id, batch.Id, DigestCategory.Normal, BaseTime));
        }

        var first = await digests.GetRecentAsync(limit: 10);
        var second = await digests.GetRecentAsync(limit: 10);

        Assert.Equal(["digest-c", "digest-b", "digest-a"], first.Select(static digest => digest.Id));
        Assert.Equal(first.Select(static digest => digest.Id), second.Select(static digest => digest.Id));
    }

    [Fact]
    public async Task GetRecentAsync_RoundTripsEveryField()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var batches = database.CreateBatchRepository();
        var digests = database.CreateDigestRepository();

        var batch = await SaveBatchAsync(batches, "batch-1");
        var expected = BuildDigest("digest-1", batch.Id, DigestCategory.Urgent, BaseTime);
        await digests.SaveAsync(expected);

        var stored = Assert.Single(await digests.GetRecentAsync(limit: 10));

        Assert.Equal(expected.Id, stored.Id);
        Assert.Equal(expected.BatchId, stored.BatchId);
        Assert.Equal(expected.Category, stored.Category);
        Assert.Equal(expected.Summary, stored.Summary);
        Assert.Equal(expected.Reason, stored.Reason);
        Assert.Equal(expected.CreatedAt, stored.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, stored.CreatedAt.Kind);
    }

    [Fact]
    public async Task GetRecentAsync_ReturnsEmptyWhenNothingSaved()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var digests = database.CreateDigestRepository();

        Assert.Empty(await digests.GetRecentAsync(limit: 10));
    }

    [Fact]
    public async Task GetRecentAsync_ClampsNegativeLimitToOne()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var batches = database.CreateBatchRepository();
        var digests = database.CreateDigestRepository();

        var batch = await SaveBatchAsync(batches, "batch-1");
        for (var index = 0; index < 3; index++)
        {
            await digests.SaveAsync(BuildDigest($"digest-{index}", batch.Id, index));
        }

        // SQLite 把负数 LIMIT 当成"不限制"，不钳制就会把整表返回——这里必须只回 1 条。
        var recent = await digests.GetRecentAsync(limit: -5);

        Assert.Equal("digest-2", Assert.Single(recent).Id);
    }

    [Fact]
    public async Task GetRecentAsync_ClampsOversizedLimitToMax()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var batches = database.CreateBatchRepository();
        var digests = database.CreateDigestRepository();

        var batch = await SaveBatchAsync(batches, "batch-1");

        // 比上限多 5 条：钳制到 1000 后应当正好返回 1000 条。
        const int Oversized = 1005;
        var rows = Enumerable.Range(0, Oversized).Select(index => new object[]
        {
            $"digest-{index:D4}",
            batch.Id,
            (int)DigestCategory.Normal,
            $"摘要 {index}",
            string.Empty,
            SqliteTimestampFormat(BaseTime.AddMinutes(index)),
        });
        await database.InsertRowsAsync("digests", "id, batch_id, category, summary, reason, created_at", rows);

        var recent = await digests.GetRecentAsync(limit: int.MaxValue);

        Assert.Equal(1000, recent.Count);
        Assert.Equal("digest-1004", recent[0].Id);
    }

    [Fact]
    public async Task GetByCategoryAsync_ReturnsOnlyRequestedCategory()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var batches = database.CreateBatchRepository();
        var digests = database.CreateDigestRepository();

        var batch = await SaveBatchAsync(batches, "batch-1");
        await digests.SaveAsync(BuildDigest("urgent-1", batch.Id, DigestCategory.Urgent, 0));
        await digests.SaveAsync(BuildDigest("todo-1", batch.Id, DigestCategory.Todo, 1));
        await digests.SaveAsync(BuildDigest("urgent-2", batch.Id, DigestCategory.Urgent, 2));

        var urgent = await digests.GetByCategoryAsync(DigestCategory.Urgent, limit: 10);

        Assert.Equal(["urgent-2", "urgent-1"], urgent.Select(static digest => digest.Id));
        Assert.All(urgent, static digest => Assert.Equal(DigestCategory.Urgent, digest.Category));
    }

    [Fact]
    public async Task GetByCategoryAsync_HonorsLimit()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var batches = database.CreateBatchRepository();
        var digests = database.CreateDigestRepository();

        var batch = await SaveBatchAsync(batches, "batch-1");
        for (var index = 0; index < 3; index++)
        {
            await digests.SaveAsync(BuildDigest($"todo-{index}", batch.Id, DigestCategory.Todo, index));
        }

        var recent = await digests.GetByCategoryAsync(DigestCategory.Todo, limit: 1);

        Assert.Equal("todo-2", Assert.Single(recent).Id);
    }

    [Fact]
    public async Task GetByCategoryAsync_ReturnsEmptyForUnusedCategory()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var batches = database.CreateBatchRepository();
        var digests = database.CreateDigestRepository();

        var batch = await SaveBatchAsync(batches, "batch-1");
        await digests.SaveAsync(BuildDigest("todo-1", batch.Id, DigestCategory.Todo, 0));

        Assert.Empty(await digests.GetByCategoryAsync(DigestCategory.Study, limit: 10));
    }

    [Fact]
    public async Task GetByCategoryAsync_RejectsUndefinedCategory()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var digests = database.CreateDigestRepository();

        // 调用方传了没定义的枚举值：要当场报参数错，而不是等读行时误报"数据库有脏数据"。
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => digests.GetByCategoryAsync((DigestCategory)99, limit: 10));
    }

    [Fact]
    public async Task GetByCategoryAsync_ClampsNegativeLimitToOne()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var batches = database.CreateBatchRepository();
        var digests = database.CreateDigestRepository();

        var batch = await SaveBatchAsync(batches, "batch-1");
        await digests.SaveAsync(BuildDigest("todo-1", batch.Id, DigestCategory.Todo, 0));
        await digests.SaveAsync(BuildDigest("todo-2", batch.Id, DigestCategory.Todo, 1));

        var recent = await digests.GetByCategoryAsync(DigestCategory.Todo, limit: 0);

        Assert.Equal("todo-2", Assert.Single(recent).Id);
    }

    [Fact]
    public async Task GetByBatchAsync_StillReturnsAscendingOrder()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var batches = database.CreateBatchRepository();
        var digests = database.CreateDigestRepository();

        var batch = await SaveBatchAsync(batches, "batch-1");
        await digests.SaveAsync(BuildDigest("digest-2", batch.Id, DigestCategory.Normal, 2));
        await digests.SaveAsync(BuildDigest("digest-0", batch.Id, DigestCategory.Normal, 0));
        await digests.SaveAsync(BuildDigest("digest-1", batch.Id, DigestCategory.Normal, 1));

        // 顺带守住"老方法没被改坏"：仍然是升序，且不带 LIMIT。
        var byBatch = await digests.GetByBatchAsync(batch.Id);

        Assert.Equal(["digest-0", "digest-1", "digest-2"], byBatch.Select(static digest => digest.Id));
    }

    private static Digest BuildDigest(string id, string batchId, DigestCategory category, DateTime createdAt) => new()
    {
        Id = id,
        BatchId = batchId,
        Category = category,
        Summary = $"摘要 {id}",
        Reason = $"理由 {id}",
        CreatedAt = createdAt,
    };

    private static Digest BuildDigest(string id, string batchId, int offsetMinutes) =>
        BuildDigest(id, batchId, DigestCategory.Normal, offsetMinutes);

    private static Digest BuildDigest(string id, string batchId, DigestCategory category, int offsetMinutes) =>
        BuildDigest(id, batchId, category, BaseTime.AddMinutes(offsetMinutes));

    /// <summary>批量写入用：与 <c>SqliteTime.ToStorage</c> 相同的定宽 ISO-8601 UTC 文本。</summary>
    private static string SqliteTimestampFormat(DateTime value) => value.ToString("O");

    private static async Task<AggregationBatch> SaveBatchAsync(BatchRepository batches, string id)
    {
        var batch = new AggregationBatch
        {
            Id = id,
            GroupId = "group-1",
            GroupName = "测试群",
            Messages = Array.Empty<Message>(),
            WindowStart = BaseTime,
            WindowEnd = BaseTime.AddMinutes(3),
            Status = BatchStatus.Completed,
        };

        await batches.SaveAsync(batch);

        return batch;
    }
}