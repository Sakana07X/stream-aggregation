using System;
using System.Collections.Generic;
using System.Reflection;
using StreamingAggregation.Core.Configuration;
using StreamingAggregation.Infrastructure.OneBot;

namespace StreamingAggregation.Tests.OneBot;

/// <summary>
/// <see cref="OneBotWebSocketClient.BuildFailureReason"/> 的组合规则测试。
/// </summary>
/// <remarks>
/// <para>
/// 这三条规则（去重、段数上限、跳数上限）防的是「异常链把状态栏撑爆」，
/// 而真实 BCL 链最多三层、顶层还常与二层一字不差，
/// 因此公开 API 构造不出能压到这些上限的链——只能直接用合成链来钉。
/// </para>
/// <para>
/// 脱敏另有端到端用例兜底（<c>StreamAsync_WhenTokenProvided_NeverExposesTokenOnAnyOutlet</c> 等），
/// 因为 <c>_activeToken</c> 只在握手期间有值，构造不出「令牌正在生效」的干净夹具。
/// 这里断言的是我们自己写的文本，不依赖 BCL 的本地化措辞。
/// </para>
/// </remarks>
public sealed class OneBotFailureReasonTests
{
    private const string Separator = " ← ";

    [Fact]
    public void BuildFailureReason_SingleLayer_ReturnsThatMessageUnchanged()
    {
        var reason = CreateClient().BuildFailureReason(new InvalidOperationException("单层失败。"));

        Assert.Equal("单层失败。", reason);
        Assert.DoesNotContain(Separator, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFailureReason_KeepsOutermostFirst_AndAppendsInnerDetail()
    {
        // 只追加、不替换：最外层必须仍是第一段，net10 的既有行为因此不变。
        var reason = CreateClient().BuildFailureReason(Chain("外层", "中层", "根因"));

        Assert.Equal($"外层{Separator}中层{Separator}根因", reason);
    }

    [Fact]
    public void BuildFailureReason_IdenticalLayers_CollapseIntoOneSegment()
    {
        // 包装异常复述内层是常态（端口不通时两层都是「无法连接到远程服务器」）。
        var reason = CreateClient().BuildFailureReason(Chain("连不上", "连不上", "连不上", "根因"));

        Assert.Equal($"连不上{Separator}根因", reason);
        Assert.Equal(1, Occurrences(reason, "连不上"));
    }

    [Fact]
    public void BuildFailureReason_DuplicatesDoNotConsumeSegmentBudget()
    {
        // 去重掉的层不该把「三段」的名额吃掉，否则真正有区分度的里层就挤不进来。
        var reason = CreateClient().BuildFailureReason(Chain("重复", "重复", "重复", "甲", "乙", "丙"));

        Assert.Equal($"重复{Separator}甲{Separator}乙", reason);
    }

    [Fact]
    public void BuildFailureReason_LongChain_KeepsAtMostThreeDistinctSegments()
    {
        var messages = new List<string>();
        for (var i = 1; i <= 200; i++)
        {
            messages.Add($"第{i}层");
        }

        var reason = CreateClient().BuildFailureReason(Chain(messages));

        Assert.Equal($"第1层{Separator}第2层{Separator}第3层", reason);
        Assert.Equal(3, reason.Split(new[] { Separator }, StringSplitOptions.None).Length);
    }

    [Fact]
    public void BuildFailureReason_SelfReferencingChain_Terminates()
    {
        // Exception.InnerException 没有公开 setter，自引用链只能经私有字段制造。
        // 这条用例的价值就在于「能跑完」：没有跳数上限时它会挂住测试宿主，而不是安静地通过。
        var looping = new InvalidOperationException("自引用。");
        var field = typeof(Exception).GetField("_innerException", BindingFlags.Instance | BindingFlags.NonPublic);

        // 运行时一旦改名就显式失败：静默跳过等于这条保险没人验证。
        Assert.True(field is not null, "Exception._innerException 不存在，跳数上限已无法被验证。");
        field!.SetValue(looping, looping);

        var reason = CreateClient().BuildFailureReason(looping);

        Assert.Equal("自引用。", reason);
    }

    [Fact]
    public void BuildFailureReason_BlankMessages_AreSkippedWithoutLeavingEmptySegments()
    {
        var reason = CreateClient().BuildFailureReason(Chain("   ", string.Empty, "真实原因"));

        Assert.Equal("真实原因", reason);
        Assert.DoesNotContain(Separator, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildFailureReason_AllMessagesBlank_FallsBackToTypeName()
    {
        // 消息全空时也不能返回空串：状态栏空白等于把失败原因重新吞回去。
        var reason = CreateClient().BuildFailureReason(new InvalidOperationException(string.Empty));

        Assert.Equal(nameof(InvalidOperationException), reason);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void BuildFailureReason_OverlongMessages_AreTruncatedToTheLengthCap()
    {
        var reason = CreateClient().BuildFailureReason(new InvalidOperationException(new string('x', 4000)));

        Assert.True(reason.Length <= 513, $"超长消息应被截断，实际 {reason.Length} 字符。");
        Assert.EndsWith("…", reason, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // 测试脚手架
    // ---------------------------------------------------------------------

    /// <summary>造一条自外到内依次为 <paramref name="messages"/> 的异常链。</summary>
    private static Exception Chain(params string[] messages) => Chain(new List<string>(messages));

    /// <summary>造一条自外到内依次为 <paramref name="messages"/> 的异常链。</summary>
    private static Exception Chain(List<string> messages)
    {
        Exception? current = null;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            current = new InvalidOperationException(messages[i], current);
        }

        return current!;
    }

    /// <summary>新建一个未连接的客户端；此时没有活动令牌，脱敏是恒等变换。</summary>
    private static OneBotWebSocketClient CreateClient() =>
        new(new OneBotConnectionSettings { Host = "127.0.0.1", Port = 65535, UseSsl = false }, () => null);

    /// <summary>统计子串出现次数。</summary>
    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;

        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}