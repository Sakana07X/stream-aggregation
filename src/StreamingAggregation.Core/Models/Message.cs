namespace StreamingAggregation.Core.Models;

/// <summary>
/// 表示一条聊天消息。
/// </summary>
public class Message
{
    /// <summary>消息唯一标识。</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>群组 ID。</summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>发送者标识。</summary>
    public string Sender { get; set; } = string.Empty;

    /// <summary>消息文本内容。</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>消息时间戳。</summary>
    public DateTime Timestamp { get; set; }
}
