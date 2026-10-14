using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamingAggregation.Infrastructure.OneBot;

/// <summary>
/// OneBot v11 协议中的固定取值。此处只描述线上格式，不含任何业务判断。
/// </summary>
public static class OneBotProtocol
{
    /// <summary>post_type：普通消息。</summary>
    public const string PostTypeMessage = "message";

    /// <summary>post_type：元事件（lifecycle、heartbeat）。</summary>
    public const string PostTypeMetaEvent = "meta_event";

    /// <summary>message_type：群消息。本项目唯一接受的消息类型。</summary>
    public const string MessageTypeGroup = "group";

    /// <summary>message_type：私聊消息。</summary>
    public const string MessageTypePrivate = "private";
}

/// <summary>
/// 消息片段的 type 取值。SnowLuma 以 <c>messageFormat: "array"</c> 配置推送，
/// 因此一条消息会是这些片段组成的数组。
/// </summary>
public static class OneBotSegmentTypes
{
    /// <summary>纯文本，唯一会被原样保留内容的类型。</summary>
    public const string Text = "text";

    /// <summary>@提及。</summary>
    public const string At = "at";

    /// <summary>QQ 表情。</summary>
    public const string Face = "face";

    /// <summary>图片。</summary>
    public const string Image = "image";

    /// <summary>语音。</summary>
    public const string Record = "record";

    /// <summary>视频。</summary>
    public const string Video = "video";

    /// <summary>文件。</summary>
    public const string File = "file";
}

/// <summary>
/// OneBot 事件的原始协议对象。只负责承载线上 JSON，不做过滤和文本合成。
/// </summary>
public sealed class OneBotRawEvent
{
    /// <summary>Unix 时间戳（秒）。部分实现会给成字符串，故用转换器兼容。</summary>
    [JsonPropertyName("time")]
    [JsonConverter(typeof(OneBotUnixSecondsConverter))]
    public long? Time { get; set; }

    /// <summary>机器人自身账号 ID。</summary>
    [JsonPropertyName("self_id")]
    [JsonConverter(typeof(OneBotIdentifierConverter))]
    public string? SelfId { get; set; }

    /// <summary>事件类型：message、meta_event、notice、request 等。</summary>
    [JsonPropertyName("post_type")]
    public string? PostType { get; set; }

    /// <summary>元事件子类：lifecycle、heartbeat。</summary>
    [JsonPropertyName("meta_event_type")]
    public string? MetaEventType { get; set; }

    /// <summary>消息类型：group、private。</summary>
    [JsonPropertyName("message_type")]
    public string? MessageType { get; set; }

    /// <summary>消息 ID。</summary>
    [JsonPropertyName("message_id")]
    [JsonConverter(typeof(OneBotIdentifierConverter))]
    public string? MessageId { get; set; }

    /// <summary>群组 ID。</summary>
    [JsonPropertyName("group_id")]
    [JsonConverter(typeof(OneBotIdentifierConverter))]
    public string? GroupId { get; set; }

    /// <summary>发送者用户 ID。</summary>
    [JsonPropertyName("user_id")]
    [JsonConverter(typeof(OneBotIdentifierConverter))]
    public string? UserId { get; set; }

    /// <summary>
    /// 消息体。SnowLuma 配置为 <c>messageFormat: "array"</c>，此处会是片段数组；
    /// 其它实现可能给纯字符串，两种形态都由转换器归一化。
    /// </summary>
    [JsonPropertyName("message")]
    [JsonConverter(typeof(OneBotMessageConverter))]
    public OneBotMessageDto? Message { get; set; }

    /// <summary>消息的原始文本，作为片段数组不可用时的兜底。</summary>
    [JsonPropertyName("raw_message")]
    public string? RawMessage { get; set; }

    /// <summary>发送者信息。</summary>
    [JsonPropertyName("sender")]
    public OneBotSenderDto? Sender { get; set; }

    /// <summary>心跳间隔（毫秒），仅 meta_event: heartbeat 携带。</summary>
    [JsonPropertyName("interval")]
    [JsonConverter(typeof(OneBotUnixSecondsConverter))]
    public long? Interval { get; set; }
}

/// <summary>
/// 归一化后的消息体：要么是片段数组，要么是纯文本，二者不会同时为空。
/// </summary>
public sealed class OneBotMessageDto
{
    /// <summary>片段数组。message 为字符串形态或缺失时为空。</summary>
    public IReadOnlyList<OneBotSegmentDto> Segments { get; init; } = Array.Empty<OneBotSegmentDto>();

    /// <summary>message 为纯字符串形态时的文本。</summary>
    public string? Text { get; init; }
}

/// <summary>
/// 单个消息片段的原始协议对象。data 的形状随 type 变化，因此保留为松散 JSON。
/// </summary>
public sealed class OneBotSegmentDto
{
    /// <summary>片段类型，如 text、at、image。</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    /// <summary>片段数据。未知的附加字段会被安全忽略。</summary>
    [JsonPropertyName("data")]
    public JsonElement? Data { get; set; }
}

/// <summary>
/// 发送者信息。群名片（card）优先于昵称。
/// </summary>
public sealed class OneBotSenderDto
{
    /// <summary>用户 ID。</summary>
    [JsonPropertyName("user_id")]
    [JsonConverter(typeof(OneBotIdentifierConverter))]
    public string? UserId { get; set; }

    /// <summary>昵称。</summary>
    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    /// <summary>群名片。</summary>
    [JsonPropertyName("card")]
    public string? Card { get; set; }

    /// <summary>群角色：owner、admin、member。</summary>
    [JsonPropertyName("role")]
    public string? Role { get; set; }
}

/// <summary>
/// 把 OneBot 中的数字型或字符串型标识统一读成字符串。
/// 同一个字段在不同实现里可能是 number 也可能是 string，两种都要接受。
/// </summary>
public sealed class OneBotIdentifierConverter : JsonConverter<string>
{
    /// <inheritdoc />
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var integral)
                    ? integral.ToString(CultureInfo.InvariantCulture)
                    : reader.GetDouble().ToString(CultureInfo.InvariantCulture);

            case JsonTokenType.String:
                return reader.GetString();

            case JsonTokenType.Null:
                return null;

            default:
                reader.Skip();
                return null;
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}

/// <summary>
/// 读取 Unix 秒级时间戳，兼容 number 与 string 两种形态，缺失或非法时返回 null。
/// </summary>
public sealed class OneBotUnixSecondsConverter : JsonConverter<long?>
{
    /// <inheritdoc />
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetInt64(out var seconds) ? seconds : null;

            case JsonTokenType.String:
                return long.TryParse(
                    reader.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                    ? parsed
                    : null;

            case JsonTokenType.Null:
                return null;

            default:
                reader.Skip();
                return null;
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteNumberValue(value.Value);
    }
}

/// <summary>
/// 把 message 字段的两种形态归一化：字符串原样保留为 <see cref="OneBotMessageDto.Text"/>，
/// 数组逐项读成片段。其它形态一律降级为空消息，不抛异常。
/// </summary>
public sealed class OneBotMessageConverter : JsonConverter<OneBotMessageDto>
{
    /// <inheritdoc />
    public override OneBotMessageDto? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return new OneBotMessageDto { Text = reader.GetString() };

            case JsonTokenType.Null:
                return null;

            case JsonTokenType.StartArray:
                return ReadSegments(ref reader, options);

            default:
                reader.Skip();
                return new OneBotMessageDto();
        }
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, OneBotMessageDto value, JsonSerializerOptions options)
    {
        if (value.Segments.Count > 0)
        {
            writer.WriteStartArray();
            foreach (var segment in value.Segments)
            {
                JsonSerializer.Serialize(writer, segment, options);
            }

            writer.WriteEndArray();
            return;
        }

        writer.WriteStringValue(value.Text);
    }

    private static OneBotMessageDto ReadSegments(ref Utf8JsonReader reader, JsonSerializerOptions options)
    {
        var segments = new List<OneBotSegmentDto>();

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray)
            {
                break;
            }

            // 数组里混入非对象元素时跳过，避免整条消息被丢弃。
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            var segment = JsonSerializer.Deserialize<OneBotSegmentDto>(ref reader, options);
            if (segment is not null)
            {
                segments.Add(segment);
            }
        }

        return new OneBotMessageDto { Segments = segments };
    }
}