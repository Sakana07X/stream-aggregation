namespace StreamingAggregation.Core.Models;

/// <summary>
/// OneBot 群消息事件（由 T2 定义，此处为编译存根）。
/// </summary>
public sealed record GroupMessageEvent(
    string MessageId,
    string GroupId,
    string SenderId,
    string Content,
    DateTime Timestamp);
