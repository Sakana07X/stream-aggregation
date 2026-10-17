using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Infrastructure.OneBot;

/// <summary>
/// 解析结果的分类。只有 <see cref="GroupMessage"/> 会进入后续聚合管线。
/// </summary>
public enum OneBotEventKind
{
    /// <summary>载荷为空或不是合法 JSON，无法判定事件类型。</summary>
    Ignored,

    /// <summary>群消息：post_type=message 且 message_type=group。</summary>
    GroupMessage,

    /// <summary>元事件：lifecycle、heartbeat。仅用于连接保活观测。</summary>
    MetaEvent,

    /// <summary>
    /// 格式合法的其它 OneBot 事件（private 消息、notice、request 等）。
    /// 本项目不对其做任何处理，仅供诊断区分"不关心"与"数据有问题"。
    /// </summary>
    Other
}

/// <summary>
/// 已解析并完成文本合成的一个消息片段。
/// </summary>
/// <param name="Type">原始片段类型，未知类型不会出现在这里。</param>
/// <param name="Text">合成到消息文本中的表示，例如 "@12345"、"[图片]"。</param>
/// <param name="IsText">是否为原样保留的纯文本片段。</param>
public sealed record OneBotSegment(string Type, string Text, bool IsText);

/// <summary>
/// 解析一次的完整结果。除事件本身外，还带上协议里存在、但 Core 事件模型未承载的信息。
/// </summary>
public sealed record OneBotParseResult
{
    /// <summary>事件分类。</summary>
    public OneBotEventKind Kind { get; init; } = OneBotEventKind.Ignored;

    /// <summary>规范化后的事件。仅群消息会填充。</summary>
    public OneBotEvent? Event { get; init; }

    /// <summary>消息 ID。协议未提供时按事件字段确定性合成。</summary>
    public string MessageId { get; init; } = string.Empty;

    /// <summary>发送者用户 ID。</summary>
    public string SenderId { get; init; } = string.Empty;

    /// <summary>发送者显示名（群名片优先于昵称），仅用于展示。</summary>
    public string SenderName { get; init; } = string.Empty;

    /// <summary>机器人自身账号 ID。</summary>
    public string SelfId { get; init; } = string.Empty;

    /// <summary>合成后的消息文本。</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>保留下来的片段。未知类型已在解析阶段剔除。</summary>
    public IReadOnlyList<OneBotSegment> Segments { get; init; } = Array.Empty<OneBotSegment>();

    /// <summary>元事件子类，仅 <see cref="OneBotEventKind.MetaEvent"/> 有值。</summary>
    public string? MetaEventType { get; init; }

    /// <summary>被忽略或失败的原因，便于排查，不参与业务判断。</summary>
    public string? Reason { get; init; }

    /// <summary>是否为可接受的群消息。</summary>
    public bool IsGroupMessage => Kind == OneBotEventKind.GroupMessage;

    /// <summary>构造一个被忽略的结果。</summary>
    public static OneBotParseResult Ignored(string reason) => new()
    {
        Kind = OneBotEventKind.Ignored,
        Reason = reason
    };
}

/// <summary>
/// OneBot 事件的纯函数解析器：原始 JSON → 规范化事件。
/// 不做 I/O、不持有状态、任何异常输入都降级为 <see cref="OneBotEventKind.Ignored"/>。
/// </summary>
public static class OneBotEventParser
{
    /// <summary>
    /// 缺失或非法时间戳的占位值。
    /// </summary>
    /// <remarks>
    /// net48 没有 <c>DateTime.UnixEpoch</c>（.NET Core 2.1+），这里显式构造
    /// Unix 纪元：1970-01-01T00:00:00Z，<see cref="DateTimeKind.Utc"/> 与原值一致。
    /// </remarks>
    public static readonly DateTime MissingTimestamp =
        new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly JsonSerializerOptions SerializerOptions = CreateOptions();

    /// <summary>
    /// 解析一行 WebSocket 载荷。仅当它是群消息时返回事件，其余一律返回 null。
    /// </summary>
    public static OneBotEvent? Parse(string? json) => ParseDetailed(json).Event;

    /// <summary>
    /// 解析一行载荷并返回完整结果，包含分类、片段与忽略原因。
    /// 这是区分群消息与 lifecycle/heartbeat 的入口。
    /// </summary>
    public static OneBotParseResult ParseDetailed(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return OneBotParseResult.Ignored("载荷为空");
        }

        OneBotRawEvent? raw;
        try
        {
            // net48 的 string.IsNullOrWhiteSpace 没有 [NotNullWhen(false)] 标注（BCL 对可空
            // 分析是"无标注"的），编译器不会自动把 json 收窄成非空，这里显式收一次。
            raw = JsonSerializer.Deserialize<OneBotRawEvent>(json!, SerializerOptions);
        }
        catch (JsonException ex)
        {
            return OneBotParseResult.Ignored($"非法 JSON：{ex.Message}");
        }

        if (raw is null)
        {
            return OneBotParseResult.Ignored("载荷解析结果为空");
        }

        return raw.PostType switch
        {
            OneBotProtocol.PostTypeMessage => ParseMessage(raw),
            OneBotProtocol.PostTypeMetaEvent => ParseMetaEvent(raw),
            _ => ClassifyOther(raw)
        };
    }

    /// <summary>
    /// 构造解析用配置。字段名已由 <see cref="JsonPropertyNameAttribute"/> 固定，
    /// 这里只开启宽松读取，避免线上多一个字段就整条失败。
    /// </summary>
    private static JsonSerializerOptions CreateOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static OneBotParseResult ParseMessage(OneBotRawEvent raw)
    {
        if (!string.Equals(raw.MessageType, OneBotProtocol.MessageTypeGroup, StringComparison.Ordinal))
        {
            // 私聊等消息格式合法但不在本项目范围内，归为 Other 而非 Ignored。
            return new OneBotParseResult
            {
                Kind = OneBotEventKind.Other,
                SelfId = raw.SelfId ?? string.Empty,
                Reason = $"不处理 message_type={raw.MessageType ?? "(空)"}"
            };
        }

        var segments = BuildSegments(raw.Message);
        var text = ComposeText(segments, raw);
        var @event = new OneBotEvent
        {
            PostType = raw.PostType ?? string.Empty,
            MessageType = raw.MessageType!,
            GroupId = raw.GroupId ?? string.Empty,
            UserId = raw.UserId ?? string.Empty,
            Message = text,
            Time = ToUtcTime(raw.Time),
            SelfId = raw.SelfId ?? string.Empty
        };

        var senderId = string.IsNullOrWhiteSpace(raw.Sender?.UserId) ? @event.UserId : raw.Sender!.UserId!;

        return new OneBotParseResult
        {
            Kind = OneBotEventKind.GroupMessage,
            Event = @event,
            MessageId = ResolveMessageId(raw, @event),
            SenderId = senderId,
            SenderName = ResolveSenderName(raw),
            SelfId = @event.SelfId,
            Text = text,
            Segments = segments
        };
    }

    /// <summary>
    /// 判定非 message、非 meta_event 的载荷。缺少 post_type 说明载荷本身不合法，
    /// 归为 Ignored；post_type 存在才认为是"合法但不处理"的事件。
    /// </summary>
    private static OneBotParseResult ClassifyOther(OneBotRawEvent raw)
    {
        if (string.IsNullOrWhiteSpace(raw.PostType))
        {
            return OneBotParseResult.Ignored("缺少 post_type");
        }

        return new OneBotParseResult
        {
            Kind = OneBotEventKind.Other,
            SelfId = raw.SelfId ?? string.Empty,
            Reason = $"不处理 post_type={raw.PostType!.Trim()}"
        };
    }

    private static OneBotParseResult ParseMetaEvent(OneBotRawEvent raw)
    {
        var metaEventType = string.IsNullOrWhiteSpace(raw.MetaEventType)
            ? string.Empty
            : raw.MetaEventType!.Trim();

        return new OneBotParseResult
        {
            Kind = OneBotEventKind.MetaEvent,
            SelfId = raw.SelfId ?? string.Empty,
            MetaEventType = metaEventType,
            Reason = metaEventType.Length == 0
                ? "未知的 meta_event 子类"
                : $"meta_event:{metaEventType}"
        };
    }

    private static List<OneBotSegment> BuildSegments(OneBotMessageDto? message)
    {
        var segments = new List<OneBotSegment>();
        if (message is null)
        {
            return segments;
        }

        if (message.Segments.Count > 0)
        {
            foreach (var dto in message.Segments)
            {
                var segment = ToSegment(dto);
                if (segment is not null)
                {
                    segments.Add(segment);
                }
            }

            return segments;
        }

        // 非数组形态（messageFormat 为 string 或实现差异）直接作为单个文本片段。
        if (!string.IsNullOrEmpty(message.Text))
        {
            segments.Add(new OneBotSegment(OneBotSegmentTypes.Text, message.Text!, IsText: true));
        }

        return segments;
    }

    private static OneBotSegment? ToSegment(OneBotSegmentDto dto)
    {
        var type = dto.Type?.Trim();
        if (string.IsNullOrEmpty(type))
        {
            return null;
        }

        var text = RenderSegment(type!, dto.Data);
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return new OneBotSegment(
            type!,
            text!,
            IsText: string.Equals(type, OneBotSegmentTypes.Text, StringComparison.Ordinal));
    }

    /// <summary>
    /// 把片段渲染成可读文本。返回 null 表示该片段不应进入文本
    /// （reply、xml、forward 等纯载荷片段，以及未知类型）。
    /// </summary>
    private static string? RenderSegment(string type, JsonElement? data) => type switch
    {
        OneBotSegmentTypes.Text => ReadString(data, "text"),
        OneBotSegmentTypes.At => RenderMention(data),
        OneBotSegmentTypes.Face => "[表情]",
        OneBotSegmentTypes.Image => "[图片]",
        OneBotSegmentTypes.Record => "[语音]",
        OneBotSegmentTypes.Video => "[视频]",
        OneBotSegmentTypes.File => "[文件]",
        "share" => "[分享]",
        "contact" => "[名片]",
        "location" => "[位置]",
        "music" => "[音乐]",
        "dice" => "[骰子]",
        "rps" => "[猜拳]",
        _ => null
    };

    private static string? RenderMention(JsonElement? data)
    {
        // 不同实现在 qq / user_id / target 之间取值，全部兼容。
        var id = ReadString(data, "qq")
            ?? ReadString(data, "user_id")
            ?? ReadString(data, "target");

        return string.IsNullOrEmpty(id) ? null : $"@{id}";
    }

    private static string? ReadString(JsonElement? data, string propertyName)
    {
        if (data is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    /// <summary>
    /// 合成最终文本：优先用片段数组；数组为空或全被忽略时回退 raw_message。
    /// </summary>
    private static string ComposeText(IReadOnlyList<OneBotSegment> segments, OneBotRawEvent raw)
    {
        if (segments.Count > 0)
        {
            var builder = new StringBuilder();
            foreach (var segment in segments)
            {
                builder.Append(segment.Text);
            }

            var composed = builder.ToString();
            if (!string.IsNullOrWhiteSpace(composed))
            {
                return composed;
            }
        }

        if (!string.IsNullOrWhiteSpace(raw.RawMessage))
        {
            return raw.RawMessage!;
        }

        return raw.Message?.Text ?? string.Empty;
    }

    private static string ResolveMessageId(OneBotRawEvent raw, OneBotEvent @event)
    {
        if (!string.IsNullOrWhiteSpace(raw.MessageId))
        {
            return raw.MessageId!;
        }

        // 协议未给 message_id 时按事件字段确定性合成，保证同一事件反复解析结果一致。
        return $"g{@event.GroupId}-u{@event.UserId}-{@event.Time.Ticks}";
    }

    private static string ResolveSenderName(OneBotRawEvent raw)
    {
        var sender = raw.Sender;
        if (sender is null)
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(sender.Card))
        {
            return sender.Card!.Trim();
        }

        return sender.Nickname?.Trim() ?? string.Empty;
    }

    private static DateTime ToUtcTime(long? unixSeconds)
    {
        if (unixSeconds is null)
        {
            return MissingTimestamp;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds.Value).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return MissingTimestamp;
        }
    }
}