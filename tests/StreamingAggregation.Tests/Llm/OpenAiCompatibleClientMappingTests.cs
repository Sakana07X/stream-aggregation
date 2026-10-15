using System.Net;
using System.Text;
using StreamingAggregation.Core;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Configuration;
using StreamingAggregation.Core.Models;
using StreamingAggregation.Infrastructure.Llm;
using StreamingAggregation.Tests.Builders;
using StreamingAggregation.Tests.Fakes;

namespace StreamingAggregation.Tests.Llm;

/// <summary>
/// <see cref="OpenAiCompatibleClient"/> 的响应映射行为测试。
/// </summary>
/// <remarks>
/// 这组测试守的是一条分界线：
/// <b>没拿到模型正文</b>才算失败（HTTP 错误、超时、外壳里取不到 content）；
/// <b>拿到了正文但没通过契约校验</b>只是降级——走 <see cref="FallbackDigestBuilder"/>
/// 出本地规则摘要，整轮仍算成功。这条线一旦被改回去，用户就会看到"模型偶尔答歪一次，
/// 整个批次直接失败"，所以用断言钉死。
/// </remarks>
public sealed class OpenAiCompatibleClientMappingTests
{
    private const string BatchId = "batch-2026-10-15-001";
    private const string GroupId = "group-123456";

    /// <summary>回退摘要的确定性主键前缀，测试要盯住"重跑幂等"这条性质。</summary>
    private const string FallbackDigestPrefix = "fb-d-";

    /// <summary>模型正文里带行动项、且完全符合契约的一条输出。</summary>
    private const string ValidContentWithAction =
        """
        {"category":"todo","summary":"张三承诺周五前提交接口文档","action":"跟进张三提交接口文档","deadline":"2026-10-17T18:00:00","importance":"high","reason":"群友明确认领了带截止时间的待办"}
        """;

    /// <summary>模型正文里没有行动项的一条输出。</summary>
    private const string ValidContentWithoutAction =
        """
        {"category":"study","summary":"群里讨论了 Redis 持久化方案的取舍","action":"","deadline":null,"importance":"medium","reason":"内容是可供后续查阅的知识整理"}
        """;

    /// <summary>缺必填字段，六个字段只给了两个。</summary>
    private const string NonConformingContent = """{"category":"todo","summary":"只写了两句"}""";

    /// <summary>外壳里根本没有 choices 数组：等价于"没拿到模型正文"。</summary>
    private const string PayloadWithoutChoices = """{"id":"chatcmpl-1","object":"chat.completion"}""";

    /// <summary>合格输出：成功、确定性主键、行动项照单全收。</summary>
    [Fact]
    public async Task ValidOutput_ProducesDeterministicDigestAndActionItem()
    {
        var diagnostics = new List<LlmDiagnostic>();
        var client = CreateClient(ChatCompletionResponse(ValidContentWithAction), diagnostics: diagnostics.Add);

        var result = await client.SummarizeAsync(BuildBatch());

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Digest);

        // 主键由批次决定，不含随机数：同一批次重跑不会插出第二条摘要。
        Assert.Equal($"llm-d-{BatchId}", result.Digest!.Id);
        Assert.Equal(BatchId, result.Digest.BatchId);
        Assert.Equal(DigestCategory.Todo, result.Digest.Category);
        Assert.Equal("张三承诺周五前提交接口文档", result.Digest.Summary);

        var actionItem = Assert.Single(result.ActionItems);
        Assert.Equal($"llm-a-{BatchId}", actionItem.Id);
        Assert.Equal(GroupId, actionItem.SourceGroupId);
        Assert.Equal(Importance.High, actionItem.Importance);
        Assert.Equal(new DateTime(2026, 10, 17, 18, 0, 0, DateTimeKind.Utc), actionItem.Deadline);

        // 合格路径不该留下回退痕迹。
        Assert.DoesNotContain(diagnostics, d => d.Level == LlmDiagnosticLevel.Warning);
    }

    /// <summary>合格但没有行动项：成功，且行动项是空列表而不是 null。</summary>
    [Fact]
    public async Task ValidOutputWithoutAction_ProducesEmptyActionItems()
    {
        var client = CreateClient(ChatCompletionResponse(ValidContentWithoutAction));

        var result = await client.SummarizeAsync(BuildBatch());

        Assert.True(result.IsSuccess);
        Assert.Equal($"llm-d-{BatchId}", result.Digest!.Id);
        Assert.NotNull(result.ActionItems);
        Assert.Empty(result.ActionItems);
    }

    /// <summary>
    /// 核心语义：拿到正文但不合格 → 回退而不是失败，且必须留 Warning 痕迹。
    /// </summary>
    [Fact]
    public async Task NonConformingOutput_FallsBackAndStillSucceeds()
    {
        var diagnostics = new List<LlmDiagnostic>();
        var client = CreateClient(ChatCompletionResponse(NonConformingContent), diagnostics: diagnostics.Add);

        var result = await client.SummarizeAsync(BuildBatch());

        // 降级不是失败：批次照常算 Completed。
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Digest);

        // 回退走本地规则，主键前缀随之改变，且仍然按批次确定。
        Assert.StartsWith(FallbackDigestPrefix, result.Digest!.Id, StringComparison.Ordinal);
        Assert.Equal($"{FallbackDigestPrefix}{BatchId}", result.Digest.Id);

        var warning = Assert.Single(diagnostics, d => d.Level == LlmDiagnosticLevel.Warning);
        Assert.Contains("本地规则摘要", warning.Message, StringComparison.Ordinal);

        // 校验明细要能看到"到底缺了什么"，否则线上无从判断模型是不是一直答歪。
        Assert.Contains("action", warning.Message, StringComparison.Ordinal);
        Assert.Contains("deadline", warning.Message, StringComparison.Ordinal);
    }

    /// <summary>回退摘要同样能产出行动项：本地规则命中待办关键词时补一个待办。</summary>
    [Fact]
    public async Task NonConformingOutput_FallbackStillProducesActionItemForTodoSignals()
    {
        var client = CreateClient(ChatCompletionResponse(NonConformingContent));
        var batch = BuildBatch("记得明天提交周报");

        var result = await client.SummarizeAsync(batch);

        Assert.True(result.IsSuccess);
        Assert.Equal(DigestCategory.Todo, result.Digest!.Category);

        var actionItem = Assert.Single(result.ActionItems);
        Assert.Equal($"fb-a-{BatchId}", actionItem.Id);
    }

    /// <summary>根本没拿到模型正文 → 仍然失败，且行动项必须是空列表而不是半成品。</summary>
    [Fact]
    public async Task HttpError_IsFailureWithEmptyActionItems()
    {
        var client = CreateClient(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom", Encoding.UTF8, "application/json"),
        });

        var result = await client.SummarizeAsync(BuildBatch());

        Assert.False(result.IsSuccess);
        Assert.Null(result.Digest);
        Assert.NotNull(result.ActionItems);
        Assert.Empty(result.ActionItems);
    }

    /// <summary>外壳里取不到 content（choices 缺失）→ 失败，不进回退。</summary>
    [Fact]
    public async Task PayloadWithoutChoices_IsFailure()
    {
        var client = CreateClient(RawPayload(PayloadWithoutChoices));

        var result = await client.SummarizeAsync(BuildBatch());

        Assert.False(result.IsSuccess);
        Assert.Null(result.Digest);
        Assert.Empty(result.ActionItems);
    }

    /// <summary>密钥缺失属于"根本没发出请求"，必须失败而不是回退。</summary>
    [Fact]
    public async Task MissingApiKey_IsFailureWithoutSendingRequest()
    {
        var handler = new StubHttpHandler(ChatCompletionResponse(ValidContentWithAction));
        var client = CreateClient(
            handler,
            apiKey: null);

        var result = await client.SummarizeAsync(BuildBatch());

        Assert.False(result.IsSuccess);
        Assert.Empty(result.ActionItems);
        Assert.Equal(0, handler.CallCount);
    }

    /// <summary>回退路径的摘要里不能出现 API Key：诊断文案与落库正文都要过脱敏。</summary>
    [Fact]
    public async Task FallbackDiagnostics_NeverLeakApiKey()
    {
        const string secret = "sk-super-secret-value";
        var client = CreateClient(
            ChatCompletionResponse(NonConformingContent),
            apiKey: secret,
            diagnostics: _ => { });

        var result = await client.SummarizeAsync(BuildBatch());

        Assert.True(result.IsSuccess);
        Assert.DoesNotContain(secret, result.RawResponse, StringComparison.Ordinal);
    }

    private static OpenAiCompatibleClient CreateClient(
        HttpResponseMessage response,
        string? apiKey = "test-api-key",
        Action<LlmDiagnostic>? diagnostics = null) =>
        CreateClient(new StubHttpHandler(response), apiKey, diagnostics);

    private static OpenAiCompatibleClient CreateClient(
        HttpMessageHandler handler,
        string? apiKey,
        Action<LlmDiagnostic>? diagnostics = null) =>
        new(
            new ModelSettings
            {
                // 回环地址允许明文 HTTP；这里走桩处理器，永远不会真的发包。
                Endpoint = "http://127.0.0.1:11434/v1",
                Model = "test-model",
                TimeoutSeconds = 5,
            },
            new StubSecretStore(apiKey),
            new HttpClient(handler),
            recorder: null,
            diagnostics: diagnostics,
            clock: new FakeClock(new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc)));

    /// <summary>把模型正文包进 OpenAI 兼容的响应外壳。</summary>
    private static HttpResponseMessage ChatCompletionResponse(string content)
    {
        var payload = $$"""
            {"id":"chatcmpl-test","object":"chat.completion","choices":[{"index":0,"message":{"role":"assistant","content":{{JsonEscape(content)}}},"finish_reason":"stop"}]}
            """;

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
    }

    /// <summary>原样返回一段响应体，不套 OpenAI 兼容外壳，用于构造"外壳本身就不合规"的场景。</summary>
    private static HttpResponseMessage RawPayload(string payload) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

    /// <summary>
    /// 把正文转成 JSON 字符串字面量。
    /// </summary>
    /// <remarks>
    /// 手工转义而不是用 <c>JsonSerializer</c>：测试要的是"字符串里长什么样模型就会收到什么"，
    /// 序列化器可能改换行、加转义，反而遮住真正要测的东西。
    /// </remarks>
    private static string JsonEscape(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal) + "\"";

    private static AggregationBatch BuildBatch(string text = "今晚八点开线上评审，别忘了提前十分钟进会议室。") => new()
    {
        Id = BatchId,
        GroupId = GroupId,
        GroupName = "测试群",
        Messages = [new MessageBuilder().WithText(text).Build()],
        WindowStart = new DateTime(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc),
        WindowEnd = new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc),
        Status = BatchStatus.Processing,
    };

    /// <summary>固定返回同一个响应的 HTTP 桩，并记录被调了几次。</summary>
    private sealed class StubHttpHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        internal int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(response);
        }
    }

    /// <summary>只读的密钥库桩，固定返回测试用 Key（或 <see langword="null"/> 表示没配）。</summary>
    private sealed class StubSecretStore(string? apiKey) : ISecretStore
    {
        public string? GetSecret(string key) => apiKey;

        public void SetSecret(string key, string value) => throw new NotSupportedException("测试桩不写密钥。");

        public void RemoveSecret(string key) => throw new NotSupportedException("测试桩不删密钥。");
    }
}