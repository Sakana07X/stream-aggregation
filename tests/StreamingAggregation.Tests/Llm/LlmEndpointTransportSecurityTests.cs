using StreamingAggregation.Infrastructure.Llm;

namespace StreamingAggregation.Tests.Llm;

/// <summary>
/// <see cref="LlmRequestBuilder.BuildChatCompletionsUri"/> 的传输协议校验测试。
/// </summary>
/// <remarks>
/// 核心安全约束：明文 HTTP 只允许指向回环地址（localhost / 127.0.0.0/8 / ::1），
/// 防止 API Key 在局域网内被嗅探；HTTPS 不限制目标。
/// </remarks>
public sealed class LlmEndpointTransportSecurityTests
{
    /// <summary>必须拒绝的端点：明文 HTTP + 非回环目标。</summary>
    public static TheoryData<string> RejectsPlaintextHttpToNonLoopbackData()
    {
        var data = new TheoryData<string>();

        // 远程主机名 + 明文 HTTP → 拒绝
        data.Add("http://api.example.com/v1");

        // 局域网 IP + 明文 HTTP → 拒绝（即使端口是常见的 Ollama 端口）
        data.Add("http://192.168.1.50:11434/v1");

        return data;
    }

    /// <summary>必须放行的端点：回环 HTTP 或 HTTPS。</summary>
    public static TheoryData<string> AllowedEndpointData()
    {
        var data = new TheoryData<string>();

        // 回环地址 + 明文 HTTP → 放行（Ollama 本机开发场景）
        data.Add("http://127.0.0.1:11434/v1");

        // localhost + 明文 HTTP → 放行
        data.Add("http://localhost:1234/v1");

        // IPv6 回环 + 明文 HTTP → 放行
        data.Add("http://[::1]:11434/v1");

        // HTTPS 对照组：非回环目标也放行
        data.Add("https://api.example.com/v1");

        return data;
    }

    /// <summary>明文 HTTP 指向非回环地址时必须抛 <see cref="ArgumentException"/>。</summary>
    [Theory]
    [MemberData(nameof(RejectsPlaintextHttpToNonLoopbackData))]
    public void RejectsPlaintextHttpToNonLoopback(string endpoint)
    {
        Assert.Throws<ArgumentException>(() => LlmRequestBuilder.BuildChatCompletionsUri(endpoint));
    }

    /// <summary>回环 HTTP 与 HTTPS 端点必须正常返回 URI。</summary>
    [Theory]
    [MemberData(nameof(AllowedEndpointData))]
    public void AllowsLoopbackHttpOrHttps(string endpoint)
    {
        var uri = LlmRequestBuilder.BuildChatCompletionsUri(endpoint);

        Assert.NotNull(uri);
        Assert.True(uri.IsAbsoluteUri, $"应返回绝对 URI，实际为 {uri}.");
        Assert.EndsWith("/chat/completions", uri.AbsolutePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>回归护栏：非法协议（ftp）仍然拒绝，不受回环白名单变更影响。</summary>
    [Fact]
    public void RejectsNonHttpScheme()
    {
        Assert.Throws<ArgumentException>(() => LlmRequestBuilder.BuildChatCompletionsUri("ftp://example.com/v1"));
    }

    /// <summary>回归护栏：非绝对 URI 仍然拒绝。</summary>
    [Fact]
    public void RejectsRelativeUri()
    {
        Assert.Throws<ArgumentException>(() => LlmRequestBuilder.BuildChatCompletionsUri("not-an-absolute-uri"));
    }
}
