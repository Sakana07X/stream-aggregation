using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using StreamingAggregation.Core.Diagnostics;
using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Infrastructure.OneBot;

/// <summary>
/// 把 OneBot 事件映射成 Core 的消息模型。
/// 映射是纯函数，不做白名单判断，白名单由 <see cref="GroupWhitelistMatcher"/> 负责。
/// </summary>
public static class OneBotMapper
{
    /// <summary>
    /// 从规范化事件构造消息。协议未提供的 message_id 会按事件字段确定性合成。
    /// </summary>
    public static Message Map(OneBotEvent source)
    {
        Guard.NotNull(source);

        return Build(
            messageId: BuildMessageId(source),
            groupId: source.GroupId,
            senderId: source.UserId,
            senderName: string.Empty,
            text: source.Message,
            timestamp: source.Time);
    }

    /// <summary>
    /// 从完整解析结果构造消息，保留协议里的真实 message_id 与发送者信息。
    /// 调用前应先用 <see cref="TryMapToMessage"/> 确认是群消息。
    /// </summary>
    public static Message Map(OneBotParseResult result)
    {
        Guard.NotNull(result);

        if (result.Event is null)
        {
            throw new ArgumentException(
                $"解析结果不含事件（Kind={result.Kind}），请改用 TryMapToMessage。",
                nameof(result));
        }

        return Build(
            messageId: result.MessageId,
            groupId: result.Event.GroupId,
            senderId: result.SenderId,
            senderName: result.SenderName,
            text: result.Text,
            timestamp: result.Event.Time);
    }

    /// <summary>
    /// 仅当结果是群消息时映射出 <see cref="Message"/>，否则返回 false。
    /// 这是"只接受 post_type=message 且 message_type=group"在映射层的落点。
    /// </summary>
    public static bool TryMapToMessage(OneBotParseResult? result, [NotNullWhen(true)] out Message? message)
    {
        if (result is null || !result.IsGroupMessage || result.Event is null)
        {
            message = null;
            return false;
        }

        message = Map(result);
        return true;
    }

    /// <summary>
    /// 仅当结果是群消息时映射出 <see cref="GroupMessageEvent"/>，否则返回 false。
    /// 这是 <see cref="Core.Abstractions.IOneBotEventSource"/> 事件流的元素形态。
    /// </summary>
    public static bool TryMapToGroupMessageEvent(
        OneBotParseResult? result,
        [NotNullWhen(true)] out GroupMessageEvent? groupMessage)
    {
        if (result is null || !result.IsGroupMessage || result.Event is null)
        {
            groupMessage = null;
            return false;
        }

        groupMessage = new GroupMessageEvent(
            result.MessageId,
            result.Event.GroupId,
            result.SenderId,
            result.Text,
            result.Event.Time);
        return true;
    }

    /// <summary>
    /// <see cref="Message.Sender"/> 取发送者用户 ID：它是聚合去重与统计需要的稳定标识，
    /// 显示名放在 <see cref="OneBotParseResult.SenderName"/>，供 UI 另行取用。
    /// </summary>
    private static Message Build(
        string messageId,
        string groupId,
        string senderId,
        string senderName,
        string text,
        DateTime timestamp)
    {
        var sender = string.IsNullOrWhiteSpace(senderId) ? senderName : senderId;

        return new Message
        {
            MessageId = messageId,
            GroupId = groupId,
            Sender = sender,
            Text = text,
            Timestamp = timestamp
        };
    }

    private static string BuildMessageId(OneBotEvent source)
        => string.Format(
            CultureInfo.InvariantCulture,
            $"g{source.GroupId}-u{source.UserId}-{source.Time.Ticks}");
}