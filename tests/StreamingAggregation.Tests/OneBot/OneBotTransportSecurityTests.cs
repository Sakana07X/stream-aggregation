using StreamingAggregation.Core.Configuration;
using StreamingAggregation.Infrastructure.OneBot;

namespace StreamingAggregation.Tests.OneBot;

/// <summary>
/// <see cref="OneBotWebSocketClient"/> 构造函数对 ws/wss 端点的协议校验测试。
/// 明文 ws:// 仅允许回环地址；wss:// 无此限制。校验在构造期完成，不需要真实套接字。
/// </summary>
public sealed class OneBotTransportSecurityTests
{
    /// <summary>非回环主机使用明文 ws:// 时必须被拒绝。</summary>
    public static TheoryData<string> RejectedWsHosts { get; } = new()
    {
        "192.168.1.10",
        "example.com",
        "10.0.0.5",
        "localhost.localdomain",
    };

    /// <summary>回环主机使用明文 ws:// 时应当放行。</summary>
    public static TheoryData<string> AllowedWsHosts { get; } = new()
    {
        "127.0.0.1",
        "127.0.0.2",
        "[::1]",
        "localhost",
    };

    /// <summary>WSS 总是允许，无论主机是否为回环。</summary>
    public static TheoryData<string> AllowedWssHosts { get; } = new()
    {
        "192.168.1.10",
        "example.com",
    };

    [Theory]
    [MemberData(nameof(RejectedWsHosts))]
    public void Constructor_RejectsPlainWsForNonLoopbackHost(string host)
    {
        var settings = new OneBotConnectionSettings { Host = host, UseSsl = false };

        var ex = Assert.Throws<ArgumentException>(
            () => new OneBotWebSocketClient(settings, () => "token"));

        Assert.Contains("回环", ex.Message);
    }

    [Theory]
    [MemberData(nameof(AllowedWsHosts))]
    public void Constructor_AllowsPlainWsForLoopbackHost(string host)
    {
        var settings = new OneBotConnectionSettings { Host = host, UseSsl = false };

        var client = new OneBotWebSocketClient(settings, () => "token");
        client.Dispose();
    }

    [Theory]
    [MemberData(nameof(AllowedWssHosts))]
    public void Constructor_AlwaysAllowsWss(string host)
    {
        var settings = new OneBotConnectionSettings { Host = host, UseSsl = true };

        var client = new OneBotWebSocketClient(settings, () => "token");
        client.Dispose();
    }
}
