using StreamingAggregation.Core.Filtering;
using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Tests.Filtering;

/// <summary>
/// <see cref="IdentityFilter"/> 测试：缺少群 ID 或发送者 ID 的消息必须被丢弃。
/// </summary>
/// <remarks>
/// 背景：OneBot 解析层采取「归一化而非丢弃」策略，缺失的 <c>group_id</c> / <c>user_id</c>
/// 会留为空串。业务管线入口必须用本过滤器拦下这些消息，否则空身份会污染去重键、
/// 落库，并在摘要提示词里渲染成空发送者。
/// </remarks>
public sealed class IdentityFilterTests
{
    private static Message Create(string messageId, string groupId, string sender) => new()
    {
        MessageId = messageId,
        GroupId = groupId,
        Sender = sender,
        Text = "会议改到下午三点",
        Timestamp = new DateTime(2026, 10, 15, 10, 0, 0, DateTimeKind.Utc),
    };

    /// <summary>群 ID 和发送者都齐全时保留。</summary>
    [Fact]
    public void Filter_KeepsMessageWithCompleteIdentity()
    {
        var msg = Create("test-msg-1", "test-group-id", "test-user-id");

        var result = new IdentityFilter().Filter([msg]);

        Assert.Single(result);
        Assert.Same(msg, result[0]);
    }

    /// <summary>群 ID 缺失时丢弃。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Filter_DropsMessageWithMissingGroupId(string groupId)
    {
        var msg = Create("test-msg-2", groupId, "test-user-id");

        var result = new IdentityFilter().Filter([msg]);

        Assert.Empty(result);
    }

    /// <summary>发送者缺失时丢弃。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Filter_DropsMessageWithMissingSender(string sender)
    {
        var msg = Create("test-msg-3", "test-group-id", sender);

        var result = new IdentityFilter().Filter([msg]);

        Assert.Empty(result);
    }

    /// <summary>两项都缺失时丢弃。</summary>
    [Fact]
    public void Filter_DropsMessageWithBothIdentitiesMissing()
    {
        var msg = Create("test-msg-4", string.Empty, string.Empty);

        var result = new IdentityFilter().Filter([msg]);

        Assert.Empty(result);
    }

    /// <summary>混合输入只保留身份完整的消息，其余原序保留。</summary>
    [Fact]
    public void Filter_KeepsOnlyValidMessagesInOrder()
    {
        var valid1 = Create("test-msg-5", "test-group-id", "test-user-1");
        var noGroup = Create("test-msg-6", string.Empty, "test-user-2");
        var valid2 = Create("test-msg-7", "test-group-id", string.Empty);
        var valid3 = Create("test-msg-8", "test-group-id", "test-user-3");

        var result = new IdentityFilter().Filter([valid1, noGroup, valid2, valid3]);

        Assert.Equal(2, result.Count);
        Assert.Same(valid1, result[0]);
        Assert.Same(valid3, result[1]);
    }

    /// <summary>空输入返回空结果，不抛异常。</summary>
    [Fact]
    public void Filter_WithEmptyInput_ReturnsEmpty()
    {
        var result = new IdentityFilter().Filter([]);

        Assert.Empty(result);
    }

    /// <summary>null 输入抛 <see cref="ArgumentNullException"/>。</summary>
    [Fact]
    public void Filter_WithNullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new IdentityFilter().Filter(null!));
    }
}