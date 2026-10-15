using StreamingAggregation.Core.Models;
using StreamingAggregation.Tests.Builders;

namespace StreamingAggregation.Tests.Persistence;

/// <summary>
/// <c>MessageRepository.GetRecentAsync</c>：不限群组的"最近 N 条"。
/// </summary>
public sealed class MessageRepositoryTests
{
    private static readonly DateTime BaseTime = new(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task GetRecentAsync_ReturnsNewestFirstAcrossGroups()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var messages = database.CreateMessageRepository();

        await SaveAsync(messages, "m-1", "group-1", 0);
        await SaveAsync(messages, "m-3", "group-2", 2);
        await SaveAsync(messages, "m-2", "group-1", 1);

        var recent = await messages.GetRecentAsync(limit: 10);

        Assert.Equal(["m-3", "m-2", "m-1"], recent.Select(static message => message.MessageId));
    }

    [Fact]
    public async Task GetRecentAsync_HonorsLimit()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var messages = database.CreateMessageRepository();

        for (var index = 0; index < 3; index++)
        {
            await SaveAsync(messages, $"m-{index}", "group-1", index);
        }

        var recent = await messages.GetRecentAsync(limit: 2);

        Assert.Equal(["m-2", "m-1"], recent.Select(static message => message.MessageId));
    }

    [Fact]
    public async Task GetRecentAsync_OrdersByMessageIdWhenTimestampsAreEqual()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var messages = database.CreateMessageRepository();

        foreach (var id in new[] { "m-a", "m-b", "m-c" })
        {
            // 同一时间戳：次级键保证行序确定，重复查询结果一致。
            await SaveAsync(messages, id, "group-1", BaseTime);
        }

        var first = await messages.GetRecentAsync(limit: 10);
        var second = await messages.GetRecentAsync(limit: 10);

        Assert.Equal(["m-c", "m-b", "m-a"], first.Select(static message => message.MessageId));
        Assert.Equal(first.Select(static message => message.MessageId), second.Select(static message => message.MessageId));
    }

    [Fact]
    public async Task GetRecentAsync_RoundTripsEveryField()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var messages = database.CreateMessageRepository();

        var expected = new MessageBuilder()
            .WithMessageId("m-1")
            .WithGroupId("group-1")
            .WithSender("张三")
            .WithText("含 \"引号\" 与中文的正文")
            .WithTimestamp(BaseTime)
            .Build();
        await messages.SaveAsync(expected);

        var stored = Assert.Single(await messages.GetRecentAsync(limit: 10));

        Assert.Equal(expected.MessageId, stored.MessageId);
        Assert.Equal(expected.GroupId, stored.GroupId);
        Assert.Equal(expected.Sender, stored.Sender);
        Assert.Equal(expected.Text, stored.Text);
        Assert.Equal(expected.Timestamp, stored.Timestamp);
        Assert.Equal(DateTimeKind.Utc, stored.Timestamp.Kind);
    }

    [Fact]
    public async Task GetRecentAsync_ReturnsEmptyWhenNothingSaved()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var messages = database.CreateMessageRepository();

        Assert.Empty(await messages.GetRecentAsync(limit: 10));
    }

    [Fact]
    public async Task GetRecentAsync_ClampsNegativeLimitToOne()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var messages = database.CreateMessageRepository();

        for (var index = 0; index < 3; index++)
        {
            await SaveAsync(messages, $"m-{index}", "group-1", index);
        }

        // 负数 LIMIT 在 SQLite 里等于"不限制"，钳制后必须只回最新的一条。
        var recent = await messages.GetRecentAsync(limit: -1);

        Assert.Equal("m-2", Assert.Single(recent).MessageId);
    }

    [Fact]
    public async Task GetRecentAsync_ClampsOversizedLimitToMax()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var messages = database.CreateMessageRepository();

        const int Oversized = 1005;
        var rows = Enumerable.Range(0, Oversized).Select(index => new object[]
        {
            $"m-{index:D4}",
            "group-1",
            "sender",
            $"正文 {index}",
            BaseTime.AddMinutes(index).ToString("O"),
        });
        await database.InsertRowsAsync("messages", "message_id, group_id, sender, text, timestamp", rows);

        var recent = await messages.GetRecentAsync(limit: int.MaxValue);

        Assert.Equal(1000, recent.Count);
        Assert.Equal("m-1004", recent[0].MessageId);
    }

    [Fact]
    public async Task GetByGroupAsync_StillReturnsAscendingOrder()
    {
        using var database = await SqliteTestDatabase.CreateAsync();
        var messages = database.CreateMessageRepository();

        await SaveAsync(messages, "m-2", "group-1", 2);
        await SaveAsync(messages, "m-0", "group-1", 0);
        await SaveAsync(messages, "other", "group-2", 1);

        // 顺带守住"老方法没被改坏"：仍只回本群、仍升序、仍不带 LIMIT。
        var byGroup = await messages.GetByGroupAsync("group-1", DateTime.MinValue);

        Assert.Equal(["m-0", "m-2"], byGroup.Select(static message => message.MessageId));
    }

    private static Task SaveAsync(
        Infrastructure.Persistence.MessageRepository messages,
        string messageId,
        string groupId,
        int offsetMinutes) =>
        messages.SaveAsync(BuildMessage(messageId, groupId, BaseTime.AddMinutes(offsetMinutes)));

    private static Task SaveAsync(
        Infrastructure.Persistence.MessageRepository messages,
        string messageId,
        string groupId,
        DateTime timestamp) =>
        messages.SaveAsync(BuildMessage(messageId, groupId, timestamp));

    private static Message BuildMessage(string messageId, string groupId, DateTime timestamp) =>
        new MessageBuilder()
            .WithMessageId(messageId)
            .WithGroupId(groupId)
            .WithTimestamp(timestamp)
            .Build();
}