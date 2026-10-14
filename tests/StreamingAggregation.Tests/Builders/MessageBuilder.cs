using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Tests.Builders;

/// <summary>
/// Message 对象的测试数据构建器。
/// 所有默认值均为明显的假数据，严禁用于生产环境。
/// </summary>
public class MessageBuilder
{
    private string _messageId = "test-message-id-001";
    private string _groupId = "test-group-id";
    private string _sender = "Test User";
    private string _text = "This is a test message.";
    private DateTime _timestamp = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>设置消息 ID。</summary>
    public MessageBuilder WithMessageId(string messageId)
    {
        _messageId = messageId;
        return this;
    }

    /// <summary>设置群组 ID。</summary>
    public MessageBuilder WithGroupId(string groupId)
    {
        _groupId = groupId;
        return this;
    }

    /// <summary>设置发送者。</summary>
    public MessageBuilder WithSender(string sender)
    {
        _sender = sender;
        return this;
    }

    /// <summary>设置消息文本。</summary>
    public MessageBuilder WithText(string text)
    {
        _text = text;
        return this;
    }

    /// <summary>设置时间戳。</summary>
    public MessageBuilder WithTimestamp(DateTime timestamp)
    {
        _timestamp = timestamp;
        return this;
    }

    /// <summary>构建 Message 对象。</summary>
    public Message Build()
    {
        return new Message
        {
            MessageId = _messageId,
            GroupId = _groupId,
            Sender = _sender,
            Text = _text,
            Timestamp = _timestamp
        };
    }
}
