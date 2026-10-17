using System.Text;
using System.Text.Json;
using StreamingAggregation.Infrastructure.OneBot;

namespace StreamingAggregation.Tests.OneBot;

/// <summary>
/// <see cref="OneBotFrameSplitter"/> 的一帧多值拆分测试。
/// </summary>
/// <remarks>
/// 一帧之内可能出现多个顶层 JSON 值（粘包），也可能在值之间夹杂坏字节。
/// 拆分器必须：按顺序逐个产出完整值、对字符串字面量内的 <c>{</c> 与转义引号免疫、
/// 跳过坏字节重新同步，并在帧末尾出现不完整值时抛出 <see cref="JsonException"/>。
/// </remarks>
public sealed class OneBotFrameSplitterTests
{
    private static IReadOnlyList<string> SplitAsText(string frame)
        => SplitAsText(Encoding.UTF8.GetBytes(frame));

    private static IReadOnlyList<string> SplitAsText(byte[] frame)
        => OneBotFrameSplitter
            .Split(frame)
            // net48 的 Encoding.GetString 没有 ReadOnlySpan<byte> / ReadOnlyMemory<byte> 重载，
            // 先 ToArray() 再解码（与 OneBotWebSocketClient 的做法一致）。
            .Select(static value => Encoding.UTF8.GetString(value.ToArray()))
            .ToList();

    /// <summary>单值帧：产出且仅产出该值。</summary>
    [Fact]
    public void Split_SingleValue_ReturnsThatValueOnly()
    {
        const string frame = """{"post_type":"message","message_id":"1"}""";

        Assert.Equal(new[] { frame }, SplitAsText(frame));
    }

    /// <summary>粘包：一帧两个紧挨着的 JSON 值必须都被拆出且保持顺序。</summary>
    [Fact]
    public void Split_TwoValuesInOneFrame_ReturnsBothInOrder()
    {
        const string first = """{"post_type":"message","message_id":"1"}""";
        const string second = """{"post_type":"message","message_id":"2"}""";

        Assert.Equal(new[] { first, second }, SplitAsText(first + second));
    }

    /// <summary>粘包：一帧三个 JSON 值必须都被拆出。</summary>
    [Fact]
    public void Split_ThreeValuesInOneFrame_ReturnsAllThreeInOrder()
    {
        const string first = """{"id":1}""";
        const string second = """{"id":2}""";
        const string third = """{"id":3}""";

        Assert.Equal(new[] { first, second, third }, SplitAsText(first + second + third));
    }

    /// <summary>
    /// 字符串字面量里的 <c>}</c> 与转义引号不得被当成结构符号：
    /// <c>{"k":"}"}{"k2":"a\"b"}</c> 必须拆成两个值。
    /// </summary>
    [Fact]
    public void Split_BracesAndEscapedQuotesInsideStrings_DoNotSplit()
    {
        const string first = """{"k":"}"}""";
        const string second = """{"k2":"a\"b"}""";

        Assert.Equal(new[] { first, second }, SplitAsText(first + second));
    }

    /// <summary>转义的反斜杠不得吃掉紧随其后的引号：<c>{"k":"a\\"}</c> 是一个完整值。</summary>
    [Fact]
    public void Split_EscapedBackslashBeforeQuote_DoesNotEndString()
    {
        // JSON 文本为 {"k":"a\\"}：\\ 是转义反斜杠，随后的 " 仍是字符串内部的普通引号。
        const string frame = "{\"k\":\"a\\\\\"}{\"k2\":2}";

        Assert.Equal(new[] { """{"k":"a\\"}""", """{"k2":2}""" }, SplitAsText(frame));
    }

    /// <summary>嵌套数组与对象应被当作单个值整体产出。</summary>
    [Fact]
    public void Split_NestedArrayValue_IsKeptAsSingleValue()
    {
        const string first = """{"message":[{"type":"text","data":{"text":"a"}}]}""";
        const string second = """{"message":[{"type":"text","data":{"text":"b"}}]}""";

        Assert.Equal(new[] { first, second }, SplitAsText(first + second));
    }

    /// <summary>顶层数组同样是一个完整的 JSON 值。</summary>
    [Fact]
    public void Split_TopLevelArrayValue_IsRecognized()
    {
        Assert.Equal(new[] { """[{"id":1},{"id":2}]""" }, SplitAsText("""[{"id":1},{"id":2}]"""));
    }

    /// <summary>值之间的空格、制表符、CR、LF 都不应被当作值产出。</summary>
    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    [InlineData(" \t\r\n  \n ")]
    public void Split_WhitespaceBetweenValues_IsIgnored(string separator)
    {
        const string first = """{"id":1}""";
        const string second = """{"id":2}""";

        var values = SplitAsText(separator + first + separator + second + separator);

        Assert.Equal(new[] { first, second }, values);
    }

    /// <summary>坏字节逐个跳过并在下一个 <c>{</c> 处重新同步，坏数据不得连累同帧内其它值。</summary>
    [Fact]
    public void Split_JunkBytes_AreSkippedAndSplitterResynchronises()
    {
        const string first = """{"id":1}""";
        const string second = """{"id":2}""";

        var frame = Encoding.UTF8.GetBytes(first + "\0\xFF\x01junk" + second);

        Assert.Equal(new[] { first, second }, SplitAsText(frame));
    }

    /// <summary>仅由空白组成的一帧不产出任何值。</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\r\n\t  ")]
    public void Split_EmptyOrWhitespaceOnlyFrame_ReturnsNothing(string frame)
        => Assert.Empty(SplitAsText(frame));

    /// <summary>空输入（零长度）必须安全返回空序列。</summary>
    [Fact]
    public void Split_EmptyPayload_ReturnsNothing()
        => Assert.Empty(OneBotFrameSplitter.Split(ReadOnlyMemory<byte>.Empty));

    /// <summary>多字节 UTF-8 中文内容不得影响结构扫描。</summary>
    [Fact]
    public void Split_MultiByteUtf8Content_DoesNotAffectScanning()
    {
        const string first = """{"message":"你好，世界 😀"}""";
        const string second = """{"message":"再见"}""";

        Assert.Equal(new[] { first, second }, SplitAsText(first + second));
    }

    /// <summary>字符串内的转义换行符不得被当成值之间的分隔空白。</summary>
    [Fact]
    public void Split_StringContainingNewline_DoesNotSplit()
    {
        // JSON 文本中的 \n 是两字节转义序列，不是真实换行。
        const string first = """{"message":"第一行\n第二行\n第三行"}""";
        const string second = """{"id":2}""";

        Assert.Equal(new[] { first, second }, SplitAsText(first + "\r\n" + second));
    }

    /// <summary>帧末尾出现不完整的值时抛出 <see cref="JsonException"/>，此前已产出的值仍然有效。</summary>
    [Fact]
    public void Split_TruncatedFinalValue_ThrowsJsonExceptionAfterYieldingEarlierValues()
    {
        const string first = """{"id":1}""";
        var yielded = new List<string>();

        var ex = Assert.Throws<JsonException>(() =>
        {
            foreach (var value in OneBotFrameSplitter.Split(Encoding.UTF8.GetBytes(first + """{"id":2""")))
            {
                yielded.Add(Encoding.UTF8.GetString(value.ToArray()));
            }
        });

        Assert.Contains("不完整", ex.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { first }, yielded);
    }

    /// <summary>产出的每个值都必须是能被 <see cref="JsonDocument"/> 解析的独立 JSON 值。</summary>
    [Fact]
    public void Split_ProducedValues_AreIndependentlyParseable()
    {
        const string first = """{"id":1,"text":"}"}""";
        const string second = """{"id":2,"text":"a\"b"}""";

        foreach (var value in OneBotFrameSplitter.Split(Encoding.UTF8.GetBytes(first + second)))
        {
            using var document = JsonDocument.Parse(value);

            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        }
    }
}