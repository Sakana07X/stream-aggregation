using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Configuration;
using StreamingAggregation.Core.Diagnostics;
using StreamingAggregation.Core.Models;
using StreamingAggregation.Infrastructure.Persistence;

namespace StreamingAggregation.Infrastructure.Llm;

/// <summary>
/// OpenAI 兼容接口的摘要客户端：<c>POST {Endpoint}/chat/completions</c>。
/// </summary>
/// <remarks>
/// <para><b>凭据</b>：API Key 只从 <see cref="ISecretStore"/>（DPAPI 实现）读取，
/// 键名默认 <see cref="DefaultApiKeySecretKey"/>，<b>不</b>使用 <see cref="ModelSettings.ApiKey"/>——
/// 即使传进来的设置对象带着明文 Key，本客户端也会忽略它。</para>
/// <para><b>端点传输安全</b>：非回环地址的模型端点必须使用 HTTPS；
/// 明文 HTTP 仅在目标是 localhost / 127.0.0.0/8 / ::1 等回环地址时允许。
/// 违反此策略的端点会在 <see cref="Prepare"/> 阶段被归一为
/// <see cref="LlmFailureKind.ConfigurationInvalid"/> 失败结果。</para>
/// <para><b>失败处理</b>：超时、网络失败、HTTP 非 2xx、响应不是预期的 JSON 外壳、
/// 配置缺失——也就是<b>根本没拿到模型正文</b>的情况——全部归一成 <see cref="LlmResult"/>
/// 里 <c>IsSuccess = false</c> 的结果；只有调用方主动取消才抛 <see cref="OperationCanceledException"/>。</para>
/// <para><b>拿到正文之后</b>：模型输出交给 <see cref="LlmResultMapper.MapOrFallback"/>
/// 校验并映射。校验不通过不会让整轮失败，而是改用 <see cref="FallbackDigestBuilder"/>
/// 的本地规则摘要（<c>Digest.Id</c> 变成 <c>fb-d-{batchId}</c>），并记一条
/// <see cref="LlmDiagnosticLevel.Warning"/> 诊断；批次因此仍算成功，
/// 只是摘要质量降级。这样"模型偶尔答非所问"不会连带整轮聚合报废。</para>
/// <para><b>不阻塞消息接收</b>：整条链路全异步，每一处 await 都带 <c>ConfigureAwait(false)</c>，
/// 在 WinUI 界面线程上调用也不会把续体切回界面线程；本类型不做任何同步等待
/// （不使用 <c>.Result</c> / <c>.Wait()</c>），也不会自己开线程池工作项。</para>
/// <para><b>生命周期</b>：注入的 <see cref="HttpClient"/> 由调用方拥有与释放，
/// 本类型既不实现 <see cref="IDisposable"/> 也不改动它的 <see cref="HttpClient.Timeout"/>——
/// 单次调用的超时由 <see cref="ModelSettings.TimeoutSeconds"/> 派生的独立计时器控制。</para>
/// </remarks>
public sealed class OpenAiCompatibleClient : ISummarizationClient
{
    /// <summary>API Key 在 <see cref="ISecretStore"/> 中的默认键名，与设置存储保持一致。</summary>
    public const string DefaultApiKeySecretKey = SettingsStore.ModelApiKeySecretKey;

    /// <summary><see cref="ModelSettings.TimeoutSeconds"/> 非法时使用的兜底超时秒数。</summary>
    public const int FallbackTimeoutSeconds = 30;

    /// <summary>响应正文读取上限，防止异常实现返回超大响应撑爆内存。</summary>
    public const int MaxResponseChars = 1_000_000;

    /// <summary>错误响应正文写进记录时的截取长度。</summary>
    public const int MaxErrorBodyChars = 500;

    private readonly Func<ModelSettings> _settingsProvider;
    private readonly ISecretStore _secretStore;
    private readonly HttpClient _httpClient;
    private readonly ILlmCallRecorder? _recorder;
    private readonly Action<LlmDiagnostic>? _diagnostics;
    private readonly LlmPromptOptions _promptOptions;
    private readonly string _apiKeySecretKey;
    private readonly IClock _clock;
    private readonly LlmResultMapper _mapper;

    /// <summary>
    /// 使用设置提供委托创建客户端。设置可在运行时被替换，<b>每次调用都会重新读取</b>。
    /// </summary>
    /// <param name="settingsProvider">提供最新 <see cref="ModelSettings"/> 的委托。</param>
    /// <param name="secretStore">密钥库（DPAPI），API Key 的唯一来源。</param>
    /// <param name="httpClient">复用的 HTTP 客户端；由调用方管理生命周期。</param>
    /// <param name="recorder">可选的调用记录写入器；为 <see langword="null"/> 时不落 <c>llm_calls</c>。</param>
    /// <param name="diagnostics">可选诊断回调，收到的是已脱敏的消息。</param>
    /// <param name="promptOptions">提示词与采样参数。</param>
    /// <param name="apiKeySecretKey">API Key 在密钥库中的键名。</param>
    /// <param name="clock">摘要创建时间与相对日期换算的时间源。</param>
    /// <param name="resultMapper">模型输出映射器；不传时按 <paramref name="clock"/> 新建一个。</param>
    public OpenAiCompatibleClient(
        Func<ModelSettings> settingsProvider,
        ISecretStore secretStore,
        HttpClient httpClient,
        ILlmCallRecorder? recorder = null,
        Action<LlmDiagnostic>? diagnostics = null,
        LlmPromptOptions? promptOptions = null,
        string apiKeySecretKey = DefaultApiKeySecretKey,
        IClock? clock = null,
        LlmResultMapper? resultMapper = null)
    {
        Guard.NotNull(settingsProvider, nameof(settingsProvider));
        Guard.NotNull(secretStore, nameof(secretStore));
        Guard.NotNull(httpClient, nameof(httpClient));
        Guard.NotNullOrWhiteSpace(apiKeySecretKey, nameof(apiKeySecretKey));

        _settingsProvider = settingsProvider;
        _secretStore = secretStore;
        _httpClient = httpClient;
        _recorder = recorder;
        _diagnostics = diagnostics;
        _promptOptions = promptOptions ?? LlmPromptOptions.Default;
        _apiKeySecretKey = apiKeySecretKey;
        _clock = clock ?? SystemClock.Instance;

        // 映射器与记录器共用同一个时钟：回退摘要的创建时间与 llm_calls 记录时间才对得上。
        _mapper = resultMapper ?? new LlmResultMapper(_clock);
    }

    /// <summary>
    /// 使用固定设置对象创建客户端。对象可以被就地修改，修改对后续调用立即生效。
    /// </summary>
    /// <param name="settings">模型设置；其中 <see cref="ModelSettings.ApiKey"/> 会被忽略。</param>
    /// <param name="secretStore">密钥库（DPAPI）。</param>
    /// <param name="httpClient">复用的 HTTP 客户端。</param>
    /// <param name="recorder">可选的调用记录写入器。</param>
    /// <param name="diagnostics">可选诊断回调。</param>
    /// <param name="promptOptions">提示词与采样参数。</param>
    /// <param name="apiKeySecretKey">API Key 在密钥库中的键名。</param>
    /// <param name="clock">摘要创建时间与相对日期换算的时间源。</param>
    /// <param name="resultMapper">模型输出映射器；不传时按 <paramref name="clock"/> 新建一个。</param>
    public OpenAiCompatibleClient(
        ModelSettings settings,
        ISecretStore secretStore,
        HttpClient httpClient,
        ILlmCallRecorder? recorder = null,
        Action<LlmDiagnostic>? diagnostics = null,
        LlmPromptOptions? promptOptions = null,
        string apiKeySecretKey = DefaultApiKeySecretKey,
        IClock? clock = null,
        LlmResultMapper? resultMapper = null)
        : this(
            () => settings ?? throw new InvalidOperationException("模型设置不能为 null。"),
            secretStore,
            httpClient,
            recorder,
            diagnostics,
            promptOptions,
            apiKeySecretKey,
            clock,
            resultMapper)
    {
        Guard.NotNull(settings, nameof(settings));
    }

    /// <inheritdoc />
    public async Task<LlmResult> SummarizeAsync(AggregationBatch batch, CancellationToken cancellationToken = default)
    {
        Guard.NotNull(batch, nameof(batch));

        var stopwatch = Stopwatch.StartNew();
        var settings = ReadSettings();
        var outcome = await InvokeAsync(batch, settings, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        await RecordAsync(batch.Id, settings.Model?.Trim() ?? string.Empty, outcome.Endpoint, outcome, stopwatch.Elapsed)
            .ConfigureAwait(false);

        return outcome.ToLlmResult();
    }

    /// <summary>
    /// 读取最新设置。提供委托来自应用代码，抛异常时降级为空设置，
    /// 后续会自然归一成配置错误，避免第三方异常泄到调用方。
    /// </summary>
    private ModelSettings ReadSettings()
    {
        try
        {
            return _settingsProvider() ?? new ModelSettings();
        }
        catch (Exception ex)
        {
            Log(LlmDiagnosticLevel.Error, "读取模型设置失败，改用空设置。", ex);
            return new ModelSettings();
        }
    }

    /// <summary>
    /// 准备一次调用：拼端点 URL、取 API Key、算超时。
    /// </summary>
    /// <returns>参数齐备的 <see cref="PreparedCall"/>，或带失败原因的同一个结构。</returns>
    private PreparedCall Prepare(ModelSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
        {
            return PreparedCall.Invalid(string.Empty, LlmFailureKind.ConfigurationInvalid, "未配置模型端点。");
        }

        Uri endpoint;
        string endpointLabel;

        try
        {
            endpoint = LlmRequestBuilder.BuildChatCompletionsUri(settings.Endpoint);
            endpointLabel = LlmRequestBuilder.DescribeEndpoint(endpoint);
        }
        catch (Exception ex) when (ex is ArgumentException or UriFormatException)
        {
            return PreparedCall.Invalid(string.Empty, LlmFailureKind.ConfigurationInvalid, ex.Message);
        }

        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            return PreparedCall.Invalid(endpointLabel, LlmFailureKind.ConfigurationInvalid, "未配置模型名。");
        }

        var apiKey = ResolveApiKey();

        return apiKey is null
            ? PreparedCall.Invalid(endpointLabel, LlmFailureKind.MissingApiKey, "密钥库中没有可用的模型 API Key。")
            : new PreparedCall(
                endpoint,
                endpointLabel,
                settings.Model.Trim(),
                apiKey,
                ResolveTimeout(settings.TimeoutSeconds),
                Failure: null);
    }

    private static TimeSpan ResolveTimeout(int configuredSeconds) =>
        TimeSpan.FromSeconds(configuredSeconds > 0 ? configuredSeconds : FallbackTimeoutSeconds);

    /// <summary>
    /// 从密钥库读取 API Key 明文，仅用于组装 Authorization 头。
    /// 返回 <see langword="null"/> 表示"没有可用凭据"，不区分"没配"与"解密失败"。
    /// </summary>
    private string? ResolveApiKey()
    {
        try
        {
            var apiKey = _secretStore.GetSecret(_apiKeySecretKey);

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return null;
            }

            // 含 CR/LF 的 Key 会污染请求头，属于配置被污染而不是可用凭据。
            if (apiKey.Any(char.IsControl))
            {
                Log(LlmDiagnosticLevel.Warning, "密钥库中的模型 API Key 含控制字符，已按不可用处理。", null);
                return null;
            }

            return apiKey;
        }
        catch (Exception ex)
        {
            Log(LlmDiagnosticLevel.Error, "从密钥库读取模型 API Key 失败。", ex);
            return null;
        }
    }

    /// <summary>准备一次调用并决定是否发起请求。</summary>
    private async Task<CallOutcome> InvokeAsync(
        AggregationBatch batch,
        ModelSettings settings,
        CancellationToken cancellationToken)
    {
        var prepared = Prepare(settings);

        if (prepared.Failure is { } failure)
        {
            return CallOutcome.Failed(prepared.EndpointLabel, failure.Kind, failure.Message, promptChars: 0);
        }

        var batchText = LlmRequestBuilder.BuildBatchText(batch, _promptOptions);
        var promptChars = LlmRequestBuilder.MeasurePromptChars(LlmRequestBuilder.DefaultSystemPrompt, batchText);

        // 全是空消息时没有可摘要的内容，直接短路，不浪费一次调用。
        if (!LlmRequestBuilder.HasSummarizableContent(batch))
        {
            return CallOutcome.Failed(
                prepared.EndpointLabel,
                LlmFailureKind.EmptyBatch,
                "批次里没有任何有效消息正文，未发起请求。",
                promptChars);
        }

        return await SendAsync(batch, prepared, batchText, promptChars, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>真正发出 HTTP 请求，并把各类异常归一成 <see cref="CallOutcome"/>。</summary>
    private async Task<CallOutcome> SendAsync(
        AggregationBatch batch,
        PreparedCall prepared,
        string batchText,
        int promptChars,
        CancellationToken cancellationToken)
    {
        var apiKey = prepared.ApiKey!;

        using var timeoutCts = new CancellationTokenSource(prepared.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        // 明文 Key 的作用域仅限本次调用，所有失败文案都在这里过一遍脱敏。
        CallOutcome Fail(LlmFailureKind kind, string message) =>
            CallOutcome.Failed(prepared.EndpointLabel, kind, Redact(message, apiKey), promptChars);

        try
        {
            using var request = BuildRequest(prepared.Endpoint!, prepared.Model, apiKey, batchText);
            using var response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);

            return await ReadResponseAsync(response, batch, prepared, apiKey, promptChars, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 调用方主动取消（关停）：按 .NET 约定向上抛，不算一次失败的调用。
            throw;
        }
        catch (OperationCanceledException ex)
        {
            return Fail(
                LlmFailureKind.Timeout,
                $"超过 {prepared.Timeout.TotalSeconds:F0} 秒未返回：{ex.Message}");
        }
        catch (HttpRequestException ex)
        {
            return Fail(LlmFailureKind.Network, DescribeHttpFailure(ex));
        }
        catch (IOException ex)
        {
            return Fail(LlmFailureKind.Network, ex.Message);
        }
        catch (SocketException ex)
        {
            return Fail(LlmFailureKind.Network, ex.Message);
        }
        catch (Exception ex)
        {
            // 兜底：构造请求等其它异常同样归一成失败结果，绝不抛给调用方。
            Log(LlmDiagnosticLevel.Error, "模型调用出现未预期异常。", ex);
            return Fail(LlmFailureKind.Unexpected, ex.Message);
        }
    }

    private HttpRequestMessage BuildRequest(Uri endpoint, string model, string apiKey, string batchText)
    {
        var body = LlmRequestBuilder.BuildRequestBody(
            model,
            LlmRequestBuilder.DefaultSystemPrompt,
            batchText,
            _promptOptions);

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(body),
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };

        // Key 只出现在这一个请求头里，不会进入日志、异常消息或 llm_calls。
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        return request;
    }

    /// <summary>读取响应：状态码、正文、模型输出与摘要映射。</summary>
    private async Task<CallOutcome> ReadResponseAsync(
        HttpResponseMessage response,
        AggregationBatch batch,
        PreparedCall prepared,
        string apiKey,
        int promptChars,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await ReadBoundedAsync(response.Content, MaxErrorBodyChars, cancellationToken).ConfigureAwait(false);
            var status = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}{DescribeStatusHint(response.StatusCode)}"
                + (body.Length > 0 ? $"：{body}" : "（无响应正文）");

            return CallOutcome.Failed(
                prepared.EndpointLabel,
                LlmFailureKind.HttpStatus,
                Redact(status, apiKey),
                promptChars);
        }

        var payload = await ReadBoundedAsync(response.Content, MaxResponseChars, cancellationToken).ConfigureAwait(false);

        // 外壳层面取不到正文＝根本没有模型输出，按失败处理。
        if (!LlmResponseParser.TryExtractContent(payload, out var content, out var extractReason))
        {
            return CallOutcome.Failed(
                prepared.EndpointLabel,
                LlmFailureKind.InvalidResponse,
                Redact(extractReason!, apiKey),
                promptChars);
        }

        // 正文拿到了，就一定会有摘要：不合格只是降级，不是失败。
        var mapping = _mapper.MapOrFallback(batch, content);

        if (mapping.UsedFallback)
        {
            // 回退必须留痕，否则"摘要质量突然变差"这种问题在现场无法与模型故障区分开。
            Log(
                LlmDiagnosticLevel.Warning,
                Redact(
                    $"模型输出未通过摘要契约校验，已改用本地规则摘要：" +
                    $"[{batch.Id}] {DescribeValidationErrors(mapping.ValidationErrors)}",
                    apiKey),
                null);
        }

        return CallOutcome.Succeeded(prepared.EndpointLabel, content, mapping, promptChars);
    }

    /// <summary>把校验失败明细压成一行，写进诊断日志。</summary>
    private static string DescribeValidationErrors(IReadOnlyList<DigestValidationError> errors)
    {
        if (errors.Count == 0)
        {
            return "校验未通过，但未给出明细。";
        }

        return string.Join("; ", errors.Select(error => error.ToString()));
    }

    /// <summary>尽力写入调用记录；记录失败不影响摘要结果，也不向外抛。</summary>
    private async Task RecordAsync(
        string batchId,
        string model,
        string endpoint,
        CallOutcome outcome,
        TimeSpan duration)
    {
        if (_recorder is null)
        {
            return;
        }

        var record = outcome.Success
            ? LlmCallRecord.Succeeded(
                batchId, model, endpoint, outcome.PromptChars, outcome.CompletionChars, duration, _clock.Now)
            : LlmCallRecord.Failed(
                batchId, model, endpoint, outcome.FailureKind, outcome.ErrorMessage ?? string.Empty,
                outcome.PromptChars, outcome.CompletionChars, duration, _clock.Now);

        try
        {
            // 诊断旁路不占用调用方的取消令牌：超时等失败路径下也要把记录完整写完。
            await _recorder.RecordAsync(record, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 记录器实现未必来自本程序集，这里再兜一层。
            Log(LlmDiagnosticLevel.Warning, "写入模型调用记录时出现异常（已忽略）。", ex);
        }
    }

    /// <summary>
    /// 有上限地读取响应正文；读到上限即停止，不会把整页内容读进内存。
    /// 异常按原样抛给调用方，由 <see cref="SendAsync"/> 归类。
    /// </summary>
    /// <remarks>
    /// net48 的三条限制：<c>HttpContent.ReadAsStreamAsync()</c> 没有
    /// <see cref="CancellationToken"/> 重载（.NET 5+）；<c>StreamReader.ReadAsync</c> 也没有
    /// <c>(Memory&lt;char&gt;, CancellationToken)</c> 与可取消的
    /// <see cref="Stream.ReadAsync(byte[], int, int, CancellationToken)"/>；
    /// 而 .NET Framework 的 <c>HttpClient</c> 在取流之前会先把整个响应体缓冲进内存
    /// （<c>ResponseHeadersRead</c> 只延后<b>响应头</b>，不提供真正的流式响应体）。
    /// 因此：取消改为在循环边界检查（单次读取最多 4 KiB，响应延迟不会因此被放大），
    /// 而"读到上限即停止、不把整页读进内存"这一条只能约束最终字符串的长度，
    /// 约束不到中间那次缓冲——这是 net48 的硬限制，不是本方法的取舍。
    /// </remarks>
    private static async Task<string> ReadBoundedAsync(HttpContent content, int maxChars, CancellationToken cancellationToken)
    {
        using var stream = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var buffer = new char[4096];
        var builder = new StringBuilder(Math.Min(maxChars, 8192));

        while (builder.Length < maxChars)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var read = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            builder.Append(buffer, 0, Math.Min(read, maxChars - builder.Length));
        }

        return builder.ToString();
    }

    /// <summary>状态码的处置建议，帮助排障又不臆测服务端原因。</summary>
    private static string DescribeStatusHint(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "（API Key 可能无效或缺少该模型权限）",
        HttpStatusCode.NotFound => "（端点路径或模型名可能有误）",
        HttpStatusCode.RequestTimeout => "（服务端超时）",

        // net48 的 HttpStatusCode 没有 TooManyRequests（.NET Core 2.1+），用 RFC 6585 的数值。
        (HttpStatusCode)429 => "（触发限流，可退避后重试）",

        >= HttpStatusCode.InternalServerError => "（服务端错误，可重试）",
        _ => string.Empty,
    };

    /// <summary>
    /// 网络异常的描述。
    /// </summary>
    /// <remarks>
    /// net48 的 <see cref="HttpRequestException"/> 既没有 <c>StatusCode</c> 也没有
    /// <c>HttpRequestError</c>（两者都是 .NET 5 才加的成员），因此拿不到状态码，
    /// 改用内层异常的类型名给出粗粒度归因（DNS 失败 / 连接被拒 / 超时各自不同的
    /// 异常类型），消息正文仍是 <see cref="Exception.Message"/> 原样。
    /// 另外 net48 的 <c>HttpClient</c> 也不会为非 2xx 抛 <see cref="HttpRequestException"/>，
    /// 那条路径由 <c>IsSuccessStatusCode</c> 分支处理。
    /// </remarks>
    private static string DescribeHttpFailure(HttpRequestException exception)
    {
        var cause = exception.InnerException;
        var kind = cause is null ? exception.GetType().Name : cause.GetType().Name;
        return $"网络错误（{kind}）：{exception.Message}";
    }

    /// <summary>兜底脱敏：即使未来新增文案，也不至于把凭据写进日志或数据库。</summary>
    /// <remarks>
    /// net48 没有 <c>string.Replace(string, string, StringComparison)</c>（.NET Core 2.1+）。
    /// 两参数的 <see cref="string.Replace(string, string)"/> 本来就是序数比较，
    /// 语义与 <see cref="StringComparison.Ordinal"/> 完全一致。
    /// </remarks>
    private static string Redact(string message, string secret) =>
        string.IsNullOrEmpty(secret)
            ? message
            : message.Replace(secret, "***");

    private void Log(LlmDiagnosticLevel level, string message, Exception? exception) =>
        _diagnostics?.Invoke(new LlmDiagnostic(level, message, exception));

    /// <summary>发起请求前的准备结果：要么参数齐备，要么带着失败原因。</summary>
    private sealed record PreparedCall(
        Uri? Endpoint,
        string EndpointLabel,
        string Model,
        string? ApiKey,
        TimeSpan Timeout,
        CallFailure? Failure)
    {
        internal static PreparedCall Invalid(string endpointLabel, LlmFailureKind kind, string message) =>
            new(null, endpointLabel, string.Empty, null, TimeSpan.Zero, new CallFailure(kind, message));
    }

    private sealed record CallFailure(LlmFailureKind Kind, string Message);

    /// <summary>单次调用的统一结果，成功与各类失败都从这里出来。</summary>
    private sealed record CallOutcome
    {
        private CallOutcome(
            bool success,
            LlmFailureKind failureKind,
            string? errorMessage,
            string endpoint,
            string content,
            int promptChars,
            int completionChars,
            Digest? digest,
            IReadOnlyList<ActionItem> actionItems)
        {
            Success = success;
            FailureKind = failureKind;
            ErrorMessage = errorMessage;
            Endpoint = endpoint;
            Content = content;
            PromptChars = promptChars;
            CompletionChars = completionChars;
            Digest = digest;
            ActionItems = actionItems;
        }

        internal bool Success { get; }

        internal LlmFailureKind FailureKind { get; }

        /// <summary>已归一化并脱敏的失败原因；成功时为 <see langword="null"/>。</summary>
        /// <summary>已脱敏的原始失败原因；成功时为 <see langword="null"/>。此处不带分类前缀。</summary>
        internal string? ErrorMessage { get; }

        internal string Endpoint { get; }

        internal string Content { get; }

        internal int PromptChars { get; }

        internal int CompletionChars { get; }

        internal Digest? Digest { get; }

        /// <summary>本轮提取出的行动项；失败时恒为空列表，不会残留半成品。</summary>
        internal IReadOnlyList<ActionItem> ActionItems { get; }

        internal static CallOutcome Succeeded(
            string endpoint,
            string content,
            DigestMapping mapping,
            int promptChars)
        {
            Guard.NotNull(mapping, nameof(mapping));

            // 映射器最多给一个行动项；没有就给空列表，消费点不必判空。
            var actionItems = mapping.ActionItem is { } action
                ? new[] { action }
                : Array.Empty<ActionItem>();

            return new CallOutcome(
                true,
                LlmFailureKind.None,
                null,
                endpoint,
                content,
                promptChars,
                content.Length,
                mapping.Digest,
                actionItems);
        }

        internal static CallOutcome Failed(
            string endpoint,
            LlmFailureKind kind,
            string message,
            int promptChars,
            int completionChars = 0) =>
            new(false, kind, message, endpoint, string.Empty, promptChars, completionChars, null, Array.Empty<ActionItem>());

        /// <summary>
        /// 转成 Core 的调用结果。<c>RawResponse</c> 成功时是模型原文，
        /// 失败时写入归一化后的失败原因（<c>LlmResult</c> 目前没有单独的错误字段）。
        /// </summary>
        internal LlmResult ToLlmResult() => Success
            ? new LlmResult
            {
                IsSuccess = true,
                RawResponse = Content,
                Digest = Digest,
                ActionItems = ActionItems,
            }
            : new LlmResult
            {
                IsSuccess = false,
                RawResponse = LlmCallRecord.ComposeError(FailureKind, ErrorMessage ?? string.Empty),
                Digest = null,
                ActionItems = Array.Empty<ActionItem>(),
            };
    }
}