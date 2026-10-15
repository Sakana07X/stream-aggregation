using StreamingAggregation.Infrastructure.OneBot;

namespace StreamingAggregation.Tests.OneBot;

/// <summary>
/// <see cref="OneBotEventParser"/> 的解析行为测试。
/// </summary>
/// <remarks>
/// 解析器是纯函数：只把原始 JSON 变成分类与规范化事件，不做 I/O、不持有状态。
/// 分类语义：群消息进入聚合管线；meta_event 仅用于连接保活观测；
/// 其余格式合法的事件归 <see cref="OneBotEventKind.Other"/>，用于区分"不关心"与"数据有问题"；
/// 载荷本身不合法（坏 JSON、缺 post_type、空载荷）归 <see cref="OneBotEventKind.Ignored"/>。
/// 所有样本均为假数据（test-group-id / test-self-id / test-message-id 等），不含真实群号或令牌。
/// </remarks>
public sealed class OneBotEventParserTests
{
    /// <summary>群消息固定外壳：post_type 与 message_type 均为群消息取值。</summary>
    private const string GroupEnvelope = """
        "post_type":"message","message_type":"group","self_id":"test-self-id",
        "group_id":"test-group-id","user_id":"test-user-id","message_id":"test-message-id",
        "time":1700000000,
        "sender":{"user_id":"test-user-id","nickname":"test-nickname","card":"test-card"}
        """;

    /// <summary>Unix 时间戳 1700000000 对应的 UTC 时刻。</summary>
    private static readonly DateTime EnvelopeTime = new(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc);

    /// <summary>缺失或非法时间戳的占位值。</summary>
    private static readonly DateTime Epoch = DateTime.UnixEpoch;

    private static string GroupJson(string message, string? rawMessage = null)
        => "{" + GroupEnvelope + ",\"message\":" + message
            + (rawMessage is null ? string.Empty : ",\"raw_message\":\"" + rawMessage + "\"")
            + "}";

    private static string Segment(string type, string? data = null)
        => data is null
            ? "{\"type\":\"" + type + "\"}"
            : "{\"type\":\"" + type + "\",\"data\":" + data + "}";

    private static string Segments(params string[] segments)
        => "[" + string.Join(",", segments) + "]";

    private static string TextSegment(string text) => Segment("text", "{\"text\":\"" + text + "\"}");

    // ---------------------------------------------------------------- 分类

    /// <summary>群消息：post_type=message 且 message_type=group 时归 GroupMessage 并补全所有字段。</summary>
    [Fact]
    public void ParseDetailed_GroupMessage_IsClassifiedAndFullyPopulated()
    {
        var json = GroupJson(Segments(TextSegment("聚合测试")));

        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal(OneBotEventKind.GroupMessage, result.Kind);
        Assert.True(result.IsGroupMessage);

        var evt = result.Event;
        Assert.NotNull(evt);
        Assert.Equal("message", evt.PostType);
        Assert.Equal("group", evt.MessageType);
        Assert.Equal("test-group-id", evt.GroupId);
        Assert.Equal("test-user-id", evt.UserId);
        Assert.Equal("test-self-id", evt.SelfId);
        Assert.Equal("聚合测试", evt.Message);
        Assert.Equal(EnvelopeTime, evt.Time);
        Assert.Equal(DateTimeKind.Utc, evt.Time.Kind);

        Assert.Equal("test-message-id", result.MessageId);
        Assert.Equal("test-user-id", result.SenderId);
        Assert.Equal("test-card", result.SenderName);
        Assert.Equal("test-self-id", result.SelfId);
        Assert.Equal("聚合测试", result.Text);
        Assert.Null(result.MetaEventType);
    }

    /// <summary>便捷入口 <c>Parse</c> 只在群消息时产出事件，且与 <c>ParseDetailed</c> 结果一致。</summary>
    [Fact]
    public void ParseDetailed_GroupMessage_FacadeParseReturnsSameEvent()
    {
        var json = GroupJson(Segments(TextSegment("同一事件")));

        var detailed = OneBotEventParser.ParseDetailed(json);
        var parsed = OneBotEventParser.Parse(json);

        Assert.NotNull(parsed);
        Assert.NotNull(detailed.Event);
        Assert.Equal(detailed.Event!.GroupId, parsed.GroupId);
        Assert.Equal(detailed.Event!.UserId, parsed.UserId);
        Assert.Equal(detailed.Event!.Message, parsed.Message);
        Assert.Equal(detailed.Event!.Time, parsed.Time);
    }

    /// <summary>非群消息：格式合法但不进入聚合管线，归 Other 且不产出事件。</summary>
    [Theory]
    [InlineData("private")]
    [InlineData("channel")]
    public void ParseDetailed_PrivateMessage_IsClassifiedAsOther(string messageType)
    {
        var json = "{\"post_type\":\"message\",\"message_type\":\"" + messageType
            + "\",\"user_id\":\"test-user-id\",\"self_id\":\"test-self-id\",\"message\":\"" + messageType + "\"}";

        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal(OneBotEventKind.Other, result.Kind);
        Assert.False(result.IsGroupMessage);
        Assert.Null(result.Event);
        Assert.Equal("test-self-id", result.SelfId);
        Assert.Contains("message_type=" + messageType, result.Reason);
    }

    /// <summary>post_type=message 但缺 message_type：载荷合法，同样归 Other。</summary>
    [Fact]
    public void ParseDetailed_MessageWithoutMessageType_IsClassifiedAsOther()
    {
        var result = OneBotEventParser.ParseDetailed("{\"post_type\":\"message\",\"message_id\":\"test-message-id\"}");

        Assert.Equal(OneBotEventKind.Other, result.Kind);
        Assert.Contains("message_type=(空)", result.Reason);
    }

    /// <summary>meta_event：lifecycle 与 heartbeat 都归 MetaEvent，并保留子类名。</summary>
    public static TheoryData<string> MetaEventTypes => new()
    {
        "lifecycle",
        "heartbeat",
    };

    [Theory]
    [MemberData(nameof(MetaEventTypes))]
    public void ParseDetailed_MetaEvent_IsClassifiedAsMetaEvent(string metaEventType)
    {
        var json = "{\"post_type\":\"meta_event\",\"meta_event_type\":\"" + metaEventType
            + "\",\"self_id\":\"test-self-id\",\"time\":1700000000}";

        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal(OneBotEventKind.MetaEvent, result.Kind);
        Assert.False(result.IsGroupMessage);
        Assert.Null(result.Event);
        Assert.Equal(metaEventType, result.MetaEventType);
        Assert.Equal("test-self-id", result.SelfId);
        Assert.Equal("meta_event:" + metaEventType, result.Reason);
    }

    /// <summary>meta_event 缺子类名：仍是 MetaEvent，但子类为空并给出诊断原因。</summary>
    [Fact]
    public void ParseDetailed_MetaEventWithoutSubType_StillMetaEventWithEmptySubType()
    {
        var result = OneBotEventParser.ParseDetailed("{\"post_type\":\"meta_event\",\"self_id\":\"test-self-id\"}");

        Assert.Equal(OneBotEventKind.MetaEvent, result.Kind);
        Assert.Equal(string.Empty, result.MetaEventType);
        Assert.Equal("未知的 meta_event 子类", result.Reason);
    }

    /// <summary>非 message、非 meta_event 的事件：归 Other，post_type 写进诊断原因。</summary>
    public static TheoryData<string> OtherPostTypes => new()
    {
        "notice",
        "request",
        "message_sent",
        "meta_event_hinted",
    };

    [Theory]
    [MemberData(nameof(OtherPostTypes))]
    public void ParseDetailed_OtherPostTypes_AreClassifiedAsOther(string postType)
    {
        var result = OneBotEventParser.ParseDetailed("{\"post_type\":\"" + postType + "\",\"self_id\":\"test-self-id\"}");

        Assert.Equal(OneBotEventKind.Other, result.Kind);
        Assert.Null(result.Event);
        Assert.Contains("post_type=" + postType, result.Reason);
    }

    /// <summary>缺 post_type：无法判定事件类型，属载荷不合法，归 Ignored。</summary>
    [Fact]
    public void ParseDetailed_MissingPostType_IsIgnored()
    {
        var result = OneBotEventParser.ParseDetailed("{\"message_type\":\"group\",\"group_id\":\"test-group-id\"}");

        Assert.Equal(OneBotEventKind.Ignored, result.Kind);
        Assert.Equal("缺少 post_type", result.Reason);
        Assert.Null(result.Event);
    }

    // ------------------------------------------------------------ 片段合成

    /// <summary>片段数组按顺序合成文本，text/at/image/face 各自给出可读表示。</summary>
    [Fact]
    public void ParseDetailed_SegmentArray_ComposesTextInDeclarationOrder()
    {
        var json = GroupJson(Segments(
            TextSegment("你好"),
            Segment("at", "{\"qq\":\"test-at-target\"}"),
            Segment("image", "{\"file\":\"test-image.png\"}"),
            Segment("face", "{\"id\":\"1\"}")));

        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal("你好@test-at-target[图片][表情]", result.Text);
        Assert.Equal("你好@test-at-target[图片][表情]", result.Event!.Message);
        Assert.Collection(
            result.Segments,
            s =>
            {
                Assert.Equal(OneBotSegmentTypes.Text, s.Type);
                Assert.Equal("你好", s.Text);
                Assert.True(s.IsText);
            },
            s =>
            {
                Assert.Equal(OneBotSegmentTypes.At, s.Type);
                Assert.Equal("@test-at-target", s.Text);
                Assert.False(s.IsText);
            },
            s =>
            {
                Assert.Equal(OneBotSegmentTypes.Image, s.Type);
                Assert.Equal("[图片]", s.Text);
                Assert.False(s.IsText);
            },
            s =>
            {
                Assert.Equal(OneBotSegmentTypes.Face, s.Type);
                Assert.Equal("[表情]", s.Text);
                Assert.False(s.IsText);
            });
    }

    /// <summary>非文本媒体片段统一渲染成占位符，便于在聚合结果里保留消息轮廓。</summary>
    public static TheoryData<string, string> MediaSegmentPlaceholders => new()
    {
        { "image", "[图片]" },
        { "face", "[表情]" },
        { "record", "[语音]" },
        { "video", "[视频]" },
        { "file", "[文件]" },
        { "share", "[分享]" },
        { "contact", "[名片]" },
        { "location", "[位置]" },
        { "music", "[音乐]" },
        { "dice", "[骰子]" },
        { "rps", "[猜拳]" },
    };

    [Theory]
    [MemberData(nameof(MediaSegmentPlaceholders))]
    public void ParseDetailed_MediaSegment_IsRenderedAsPlaceholder(string type, string expected)
    {
        var result = OneBotEventParser.ParseDetailed(
            GroupJson(Segments(Segment(type, "{\"id\":\"test-payload\"}"))));

        var segment = Assert.Single(result.Segments);
        Assert.Equal(type, segment.Type);
        Assert.Equal(expected, segment.Text);
        Assert.False(segment.IsText);
        Assert.Equal(expected, result.Text);
    }

    /// <summary>message 为纯字符串形态：整串作为唯一的原样文本片段。</summary>
    [Fact]
    public void ParseDetailed_MessageAsPlainString_IsKeptAsSingleTextSegment()
    {
        var json = GroupJson("\"纯字符串形态的群消息\"");

        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal(OneBotEventKind.GroupMessage, result.Kind);
        Assert.Equal("纯字符串形态的群消息", result.Text);
        Assert.Equal("纯字符串形态的群消息", result.Event!.Message);

        var segment = Assert.Single(result.Segments);
        Assert.Equal(OneBotSegmentTypes.Text, segment.Type);
        Assert.Equal("纯字符串形态的群消息", segment.Text);
        Assert.True(segment.IsText);
    }

    /// <summary>at 片段：qq / user_id / target 三种取名，以及数字型 ID 都必须被识别。</summary>
    public static TheoryData<string, string> MentionIdAliases => new()
    {
        { "{\"qq\":\"test-at-qq\"}", "@test-at-qq" },
        { "{\"qq\":12345}", "@12345" },
        { "{\"user_id\":\"test-at-user\"}", "@test-at-user" },
        { "{\"user_id\":23456}", "@23456" },
        { "{\"target\":\"test-at-target\"}", "@test-at-target" },
        { "{\"target\":34567}", "@34567" },
    };

    [Theory]
    [MemberData(nameof(MentionIdAliases))]
    public void ParseDetailed_AtSegment_ReadsIdFromAnyAlias(string data, string expected)
    {
        var result = OneBotEventParser.ParseDetailed(GroupJson(Segments(Segment("at", data))));

        var segment = Assert.Single(result.Segments);
        Assert.Equal(OneBotSegmentTypes.At, segment.Type);
        Assert.Equal(expected, segment.Text);
        Assert.Equal(expected, result.Text);
    }

    /// <summary>at 片段：三个别名同时出现时按 qq → user_id → target 的固定优先级取第一个。</summary>
    public static TheoryData<string, string> MentionAliasPriorities => new()
    {
        { "{\"qq\":\"a\",\"user_id\":\"b\",\"target\":\"c\"}", "@a" },
        { "{\"user_id\":\"b\",\"target\":\"c\"}", "@b" },
        { "{\"target\":\"c\"}", "@c" },
        { "{\"qq\":null,\"user_id\":\"b\",\"target\":\"c\"}", "@b" },
        { "{\"qq\":true,\"user_id\":\"b\"}", "@b" },
        { "{\"qq\":[1],\"target\":\"c\"}", "@c" },
    };

    [Theory]
    [MemberData(nameof(MentionAliasPriorities))]
    public void ParseDetailed_AtSegment_PrefersHigherPriorityAlias(string data, string expected)
    {
        var result = OneBotEventParser.ParseDetailed(GroupJson(Segments(Segment("at", data))));

        Assert.Equal(expected, result.Text);
    }

    /// <summary>at 片段取不到任何可用别名 ID：整段丢弃，后续文本片段不受影响。</summary>
    [Theory]
    [InlineData("{\"qq\":\"\"}")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    [InlineData("\"just-a-string\"")]
    public void ParseDetailed_AtSegmentWithoutUsableId_IsDropped(string data)
    {
        var json = GroupJson(Segments(Segment("at", data), TextSegment("可见文本")));

        var result = OneBotEventParser.ParseDetailed(json);

        var segment = Assert.Single(result.Segments);
        Assert.Equal(OneBotSegmentTypes.Text, segment.Type);
        Assert.Equal("可见文本", result.Text);
    }

    /// <summary>message 是空数组：片段为空，回退 raw_message。</summary>
    [Fact]
    public void ParseDetailed_EmptySegmentArray_FallsBackToRawMessage()
    {
        var json = GroupJson("[]", "兜底文本 test-raw-message");

        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal(OneBotEventKind.GroupMessage, result.Kind);
        Assert.Empty(result.Segments);
        Assert.Equal("兜底文本 test-raw-message", result.Text);
        Assert.Equal("兜底文本 test-raw-message", result.Event!.Message);
    }

    /// <summary>片段全被剔除：同样回退 raw_message。</summary>
    [Fact]
    public void ParseDetailed_AllSegmentsDropped_FallsBackToRawMessage()
    {
        var json = GroupJson(Segments(Segment("reply"), Segment("xml")), "兜底文本 test-raw-message");

        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Empty(result.Segments);
        Assert.Equal("兜底文本 test-raw-message", result.Text);
    }

    /// <summary>片段合成出纯空白：视为无效文本，回退 raw_message。</summary>
    [Fact]
    public void ParseDetailed_WhitespaceOnlyComposedText_FallsBackToRawMessage()
    {
        var json = GroupJson(Segments(TextSegment("   ")), "兜底文本 test-raw-message");

        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Single(result.Segments);
        Assert.Equal("兜底文本 test-raw-message", result.Text);
    }

    /// <summary>没有任何可用文本（无片段、无 raw_message）：得到空串而不是 null。</summary>
    public static TheoryData<string> MessagesWithoutUsableText => new()
    {
        "[]",
        "null",
        "123",
        "{}",
    };

    [Theory]
    [MemberData(nameof(MessagesWithoutUsableText))]
    public void ParseDetailed_NoUsableText_ProducesEmptyMessage(string message)
    {
        var result = OneBotEventParser.ParseDetailed(GroupJson(message));

        Assert.Equal(OneBotEventKind.GroupMessage, result.Kind);
        Assert.Empty(result.Segments);
        Assert.Equal(string.Empty, result.Text);
        Assert.Equal(string.Empty, result.Event!.Message);
    }

    /// <summary>完全缺少 message 字段：仍是群消息，文本为空。</summary>
    [Fact]
    public void ParseDetailed_MissingMessageField_IsGroupMessageWithEmptyText()
    {
        var json = "{" + GroupEnvelope + "}";

        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal(OneBotEventKind.GroupMessage, result.Kind);
        Assert.Empty(result.Segments);
        Assert.Equal(string.Empty, result.Text);
    }

    /// <summary>数组里混入非对象元素：跳过它们，其余片段照常解析。</summary>
    [Fact]
    public void ParseDetailed_SegmentArrayWithNonObjectItems_KeepsObjectItems()
    {
        var json = GroupJson("[1,\"裸字符串\",null," + TextSegment("保留文本") + "]");

        var result = OneBotEventParser.ParseDetailed(json);

        var segment = Assert.Single(result.Segments);
        Assert.Equal("保留文本", segment.Text);
        Assert.Equal("保留文本", result.Text);
    }

    /// <summary>未知或纯载荷片段类型不得进入文本，也不得让整条消息失败。</summary>
    public static TheoryData<string> UnknownSegmentTypes => new()
    {
        "unknown_thing",
        "reply",
        "xml",
        "forward",
        "node",
        "markdown",
        "TEXT",
        "Text",
        "",
        "   ",
    };

    [Theory]
    [MemberData(nameof(UnknownSegmentTypes))]
    public void ParseDetailed_UnknownSegmentType_IsDropped(string type)
    {
        var json = GroupJson(Segments(Segment(type, "{\"text\":\"不该出现\"}"), TextSegment("保留文本")));

        var result = OneBotEventParser.ParseDetailed(json);

        var segment = Assert.Single(result.Segments);
        Assert.Equal(OneBotSegmentTypes.Text, segment.Type);
        Assert.Equal("保留文本", segment.Text);
        Assert.Equal("保留文本", result.Text);
    }

    /// <summary>片段缺少 type 字段：安全丢弃。</summary>
    [Fact]
    public void ParseDetailed_SegmentWithoutType_IsDropped()
    {
        var json = GroupJson(Segments("{\"data\":{\"text\":\"不该出现\"}}", TextSegment("保留文本")));

        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal("保留文本", Assert.Single(result.Segments).Text);
        Assert.Equal("保留文本", result.Text);
    }

    /// <summary>text 片段缺 text 数据：丢弃该段而不是合成空文本。</summary>
    [Theory]
    [InlineData("{\"text\":null}")]
    [InlineData("{\"font\":1}")]
    [InlineData("null")]
    public void ParseDetailed_TextSegmentWithoutText_IsDropped(string data)
    {
        var json = GroupJson(Segments(Segment("text", data), TextSegment("保留文本")));

        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal("保留文本", Assert.Single(result.Segments).Text);
    }

    /// <summary>type 前后空白会被裁掉，因此带空白的 text 仍按文本处理。</summary>
    [Fact]
    public void ParseDetailed_SegmentTypeWithSurroundingWhitespace_IsTrimmed()
    {
        var result = OneBotEventParser.ParseDetailed(
            GroupJson(Segments(Segment(" text ", "{\"text\":\"带空白类型\"}"))));

        var segment = Assert.Single(result.Segments);
        Assert.Equal(OneBotSegmentTypes.Text, segment.Type);
        Assert.Equal("带空白类型", result.Text);
    }

    // -------------------------------------------------- 标识、时间与 sender

    /// <summary>数字型标识（self_id / group_id / user_id / message_id）一律归一成字符串。</summary>
    [Fact]
    public void ParseDetailed_NumericIdentifiers_AreNormalizedToStrings()
    {
        const string json = """
            {"post_type":"message","message_type":"group","self_id":1000001,"group_id":2000002,
             "user_id":3000003,"message_id":4000004,"time":1700000000,
             "sender":{"user_id":3000003,"nickname":"test-nickname"}}
            """;

        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal(OneBotEventKind.GroupMessage, result.Kind);

        var evt = result.Event;
        Assert.NotNull(evt);
        Assert.Equal("1000001", evt.SelfId);
        Assert.Equal("2000002", evt.GroupId);
        Assert.Equal("3000003", evt.UserId);

        Assert.Equal("1000001", result.SelfId);
        Assert.Equal("4000004", result.MessageId);
        Assert.Equal("3000003", result.SenderId);
        Assert.Equal("test-nickname", result.SenderName);
    }

    /// <summary>缺 time：时间戳回退 UnixEpoch。</summary>
    [Fact]
    public void ParseDetailed_MissingTime_FallsBackToUnixEpoch()
    {
        // 与 GroupEnvelope 相同但刻意不带 time 字段。
        const string json = """
            {"post_type":"message","message_type":"group","self_id":"test-self-id",
             "group_id":"test-group-id","user_id":"test-user-id","message_id":"test-message-id",
             "message":[{"type":"text","data":{"text":"无时间戳"}}]}
            """;

        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal(Epoch, result.Event!.Time);
        Assert.Equal(OneBotEventParser.MissingTimestamp, result.Event!.Time);
        Assert.Equal(DateTimeKind.Utc, result.Event!.Time.Kind);
    }

    /// <summary>time 越界、非数字字符串或 null：一律回退 UnixEpoch，不抛异常。</summary>
    public static TheoryData<string> UnusableTimeFragments => new()
    {
        "99999999999999",
        "-99999999999999",
        "\"not-a-time\"",
        "null",
        "{}",
    };

    [Theory]
    [MemberData(nameof(UnusableTimeFragments))]
    public void ParseDetailed_UnusableTime_FallsBackToUnixEpoch(string time)
    {
        var json = "{\"post_type\":\"message\",\"message_type\":\"group\",\"group_id\":\"test-group-id\","
            + "\"user_id\":\"test-user-id\",\"time\":" + time
            + ",\"message\":" + Segments(TextSegment("时间异常")) + "}";

        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal(OneBotEventKind.GroupMessage, result.Kind);
        Assert.Equal(Epoch, result.Event!.Time);
    }

    /// <summary>time 为字符串形态：协议差异须被容忍，仍解析成同一时刻。</summary>
    [Fact]
    public void ParseDetailed_TimeAsString_IsAccepted()
    {
        var json = "{\"post_type\":\"message\",\"message_type\":\"group\",\"group_id\":\"test-group-id\","
            + "\"user_id\":\"test-user-id\",\"time\":\"1700000000\",\"message\":" + Segments(TextSegment("字符串时间戳")) + "}";

        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal(EnvelopeTime, result.Event!.Time);
    }

    /// <summary>sender.card 优先于 sender.nickname。</summary>
    [Fact]
    public void ParseDetailed_SenderCard_TakesPrecedenceOverNickname()
    {
        const string json = """
            {"post_type":"message","message_type":"group","group_id":"test-group-id","user_id":"test-user-id",
             "sender":{"user_id":"test-user-id","nickname":"test-nickname","card":"test-group-card","role":"member"}}
            """;

        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal("test-group-card", result.SenderName);
    }

    /// <summary>card 为空白：视为未设置，回退 nickname。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseDetailed_BlankSenderCard_FallsBackToNickname(string card)
    {
        var json = "{\"post_type\":\"message\",\"message_type\":\"group\",\"group_id\":\"test-group-id\","
            + "\"user_id\":\"test-user-id\",\"sender\":{\"user_id\":\"test-user-id\","
            + "\"nickname\":\"test-nickname\",\"card\":\"" + card + "\"}}";

        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal("test-nickname", result.SenderName);
    }

    /// <summary>sender.card 前后空白会被裁掉。</summary>
    [Fact]
    public void ParseDetailed_SenderCard_IsTrimmed()
    {
        var json = "{\"post_type\":\"message\",\"message_type\":\"group\",\"group_id\":\"test-group-id\","
            + "\"user_id\":\"test-user-id\",\"sender\":{\"nickname\":\"test-nickname\",\"card\":\"  test-card  \"}}";

        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal("test-card", result.SenderName);
    }

    /// <summary>没有 sender：SenderName 与 SenderId 均回退到顶层 user_id，不会是 null。</summary>
    [Fact]
    public void ParseDetailed_MissingSender_FallsBackToTopLevelUserId()
    {
        var json = "{\"post_type\":\"message\",\"message_type\":\"group\",\"group_id\":\"test-group-id\","
            + "\"user_id\":\"test-user-id\",\"message\":" + Segments(TextSegment("无 sender")) + "}";

        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal(string.Empty, result.SenderName);
        Assert.Equal("test-user-id", result.SenderId);
    }

    /// <summary>sender.user_id 为空白：回退顶层 user_id。</summary>
    [Fact]
    public void ParseDetailed_BlankSenderId_FallsBackToTopLevelUserId()
    {
        var json = "{\"post_type\":\"message\",\"message_type\":\"group\",\"group_id\":\"test-group-id\","
            + "\"user_id\":\"test-user-id\",\"sender\":{\"user_id\":\"  \",\"nickname\":\"test-nickname\"}}";

        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal("test-user-id", result.SenderId);
    }

    /// <summary>缺 message_id：按事件字段确定性合成，反复解析结果一致。</summary>
    [Fact]
    public void ParseDetailed_MissingMessageId_IsSynthesisedDeterministically()
    {
        var json = "{\"post_type\":\"message\",\"message_type\":\"group\",\"group_id\":\"test-group-id\","
            + "\"user_id\":\"test-user-id\",\"time\":1700000000,\"message\":" + Segments(TextSegment("无 message_id")) + "}";

        var first = OneBotEventParser.ParseDetailed(json);
        var second = OneBotEventParser.ParseDetailed(json);

        var expected = "gtest-group-id-utest-user-id-" + EnvelopeTime.Ticks;
        Assert.Equal(expected, first.MessageId);
        Assert.Equal(first.MessageId, second.MessageId);
    }

    // ------------------------------------------------------------ 非法载荷

    /// <summary>坏 JSON：截断、大括号不配对、尾随垃圾、单引号等一律降级为 Ignored 且不抛异常。</summary>
    public static TheoryData<string> MalformedJsonPayloads => new()
    {
        "{",
        "}",
        "{\"post_type\":\"message\"",
        "{\"post_type\":\"message\",\"message_type\":\"group\"",
        "{\"post_type\": }",
        "{\"post_type\" \"message\"}",
        "{\"post_type\":'message'}",
        "{\"post_type\":\"message\"} trailing",
        "}{",
        "[{\"post_type\":\"message\"}",
        "{\"post_type\":\"message\",\"time\":}",
        "not json at all",
        "{\"message\":[{\"type\":\"text\",}",
    };

    [Theory]
    [MemberData(nameof(MalformedJsonPayloads))]
    public void ParseDetailed_MalformedJson_IsIgnoredWithoutThrowing(string json)
    {
        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal(OneBotEventKind.Ignored, result.Kind);
        Assert.False(result.IsGroupMessage);
        Assert.Null(result.Event);
        Assert.NotNull(result.Reason);
    }

    /// <summary>合法 JSON 但不是事件对象：null、数组、标量都归 Ignored。</summary>
    public static TheoryData<string> NonObjectJsonPayloads => new()
    {
        "null",
        "[]",
        "[{\"post_type\":\"message\",\"message_type\":\"group\"}]",
        "123",
        "\"字符串\"",
        "true",
    };

    [Theory]
    [MemberData(nameof(NonObjectJsonPayloads))]
    public void ParseDetailed_NonObjectJson_IsIgnoredWithoutThrowing(string json)
    {
        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal(OneBotEventKind.Ignored, result.Kind);
        Assert.Null(result.Event);
    }

    /// <summary>空载荷：null、空串、纯空白一律归 Ignored。</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n\t")]
    public void ParseDetailed_NullOrWhitespaceInput_IsIgnored(string? json)
    {
        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal(OneBotEventKind.Ignored, result.Kind);
        Assert.Equal("载荷为空", result.Reason);
    }

    /// <summary>非法输入时便捷入口只返回 null，不把异常抛给调用方。</summary>
    public static TheoryData<string?> PayloadsWithoutEvent => new()
    {
        null,
        "",
        "   ",
        "not json at all",
        "null",
        "[]",
        "{\"post_type\":\"meta_event\",\"meta_event_type\":\"heartbeat\"}",
        "{\"post_type\":\"message\",\"message_type\":\"private\"}",
        "{\"post_type\":\"notice\",\"notice_type\":\"group_increase\"}",
        "{\"message_type\":\"group\"}",
    };

    [Theory]
    [MemberData(nameof(PayloadsWithoutEvent))]
    public void Parse_NonGroupPayload_ReturnsNullWithoutThrowing(string? json)
        => Assert.Null(OneBotEventParser.Parse(json));

    /// <summary>宽松读取：尾随逗号与注释行不影响解析。</summary>
    [Fact]
    public void ParseDetailed_TolerantReading_IgnoresTrailingCommaAndComments()
    {
        var json = """
            {
              // 实现差异产生的注释行
              "post_type":"message",
              "message_type":"group",
              "group_id":"test-group-id",
              "user_id":"test-user-id",
              "message":[{"type":"text","data":{"text":"宽松解析"}} ,],
            }
            """;

        var result = OneBotEventParser.ParseDetailed(json);

        Assert.Equal(OneBotEventKind.GroupMessage, result.Kind);
        Assert.Equal("宽松解析", result.Text);
    }
}