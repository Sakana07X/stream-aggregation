using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Tests.Builders;

/// <summary>
/// OneBot 原始事件的测试数据构建器。
/// 所有默认值均为明显的假数据，严禁用于生产环境。
/// </summary>
public class OneBotEventBuilder
{
    private string _postType = "message";
    private string _messageType = "group";
    private string _groupId = "test-group-id";
    private string _userId = "test-user-id";
    private string _message = "This is a test message.";
    private DateTime _time = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private string _selfId = "test-self-id";

    /// <summary>设置事件类型。</summary>
    public OneBotEventBuilder WithPostType(string postType)
    {
        _postType = postType;
        return this;
    }

    /// <summary>设置消息类型。</summary>
    public OneBotEventBuilder WithMessageType(string messageType)
    {
        _messageType = messageType;
        return this;
    }

    /// <summary>设置群组 ID。</summary>
    public OneBotEventBuilder WithGroupId(string groupId)
    {
        _groupId = groupId;
        return this;
    }

    /// <summary>设置用户 ID。</summary>
    public OneBotEventBuilder WithUserId(string userId)
    {
        _userId = userId;
        return this;
    }

    /// <summary>设置消息内容。</summary>
    public OneBotEventBuilder WithMessage(string message)
    {
        _message = message;
        return this;
    }

    /// <summary>设置事件时间。</summary>
    public OneBotEventBuilder WithTime(DateTime time)
    {
        _time = time;
        return this;
    }

    /// <summary>设置机器人自身 ID。</summary>
    public OneBotEventBuilder WithSelfId(string selfId)
    {
        _selfId = selfId;
        return this;
    }

    /// <summary>构建 OneBotEvent 对象。</summary>
    public OneBotEvent Build()
    {
        return new OneBotEvent
        {
            PostType = _postType,
            MessageType = _messageType,
            GroupId = _groupId,
            UserId = _userId,
            Message = _message,
            Time = _time,
            SelfId = _selfId
        };
    }
}
