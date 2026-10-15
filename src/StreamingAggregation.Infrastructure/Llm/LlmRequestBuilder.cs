using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using StreamingAggregation.Core.Configuration;
using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Infrastructure.Llm;

/// <summary>
/// 请求体组装的可调参数。
/// </summary>
public sealed record LlmPromptOptions
{
    /// <summary>批次文本的字符上限，超出部分会被截断，防止单次提示词无限膨胀。</summary>
    public int MaxBatchChars { get; init; } = 12_000;

    /// <summary>采样温度；摘要类任务需要稳定输出，默认偏低。</summary>
    public double Temperature { get; init; } = 0.2;

    /// <summary>最大输出 token 数；小于等于 0 表示不下发该字段，由服务端默认值决定。</summary>
    public int MaxTokens { get; init; }

    /// <summary>默认参数。</summary>
    public static LlmPromptOptions Default { get; } = new();
}

/// <summary>
/// 构建 OpenAI 兼容 <c>/chat/completions</c> 请求所需的 URL、提示词与 JSON 请求体。
/// </summary>
/// <remarks>
/// <para>
/// 请求体固定为两条消息：<c>system</c> 放分类契约，<c>user</c> 放批次文本；
/// <c>stream</c> 恒为 <see langword="false"/>，因为摘要需要一次性拿到完整 JSON 才能解析。
/// </para>
/// <para>
/// 本类型只负责"发什么"，不负责"发得出去吗"：网络与 HTTP 层错误由
/// <see cref="OpenAiCompatibleClient"/> 归一处理。
/// </para>
/// </remarks>
public static class LlmRequestBuilder
{
    /// <summary>OpenAI 兼容接口的相对路径。</summary>
    public const string ChatCompletionsPath = "/chat/completions";

    /// <summary>
    /// 分类与摘要的输出契约，放在 system 消息里。
    /// </summary>
    /// <remarks>
    /// 六个字段与 <see cref="DigestSchemaValidator"/> 的契约一一对应：
    /// 少写任何一个必填字段（<c>category</c> / <c>summary</c> / <c>action</c> /
    /// <c>deadline</c> / <c>reason</c>）都会让整轮校验失败并回退到本地规则，
    /// 所以这里把类型、取值范围和空值写法都写死。示例给的是模型最容易照抄的形状。
    /// </remarks>
    public const string DefaultSystemPrompt = """
        你是一个 QQ 群聊摘要助手。请阅读下面某个群在一个时间窗口内的聊天记录，输出一条 JSON 摘要。
        规则：
        1. 只输出一个 JSON 对象，不要输出 Markdown 代码块、前后缀说明或任何多余文字。
        2. JSON 必须同时包含以下六个字段，字段名不要改动，也不要省略或新增字段：
           - category：字符串，取值必须是 "urgent"、"todo"、"study"、"notice"、"normal" 之一。
             urgent=需要立刻处理；todo=需要后续跟进的待办；study=值得留存的知识点；notice=公告或知会；normal=其它。
           - summary：字符串，一句话中文摘要，不超过 100 字，要覆盖群名与核心内容。
           - action：字符串，一句话写清要做什么，不超过 200 字；本轮没有需要跟进的事时写空字符串 ""。
           - deadline：字符串或 null，action 的截止时间；没有明确截止时间时写 null。
             有截止时间时用 "yyyy-MM-ddTHH:mm:ss" 格式，例如 "2026-10-17T18:00:00"，
             不要写"明天""下周五"这类相对表述。
           - importance：字符串，取值必须是 "high"、"medium"、"low" 之一。
             high=当天内就要处理；medium=几天内跟进；low=有空再看。action 为空字符串时填 "medium"。
           - reason：字符串，一句话说明为什么归到该分类，不超过 100 字。
        3. 只能使用记录里出现的信息，不要臆造人物、事件、时间或结论；无法判断时 category 填 "normal"。
        4. 空消息、没有实质内容的闲聊：summary 写"本时段无实质内容"，category 填 "normal"，
           action 写 ""，deadline 填 null。

        有行动项时输出：
        {"category":"todo","summary":"张三承诺周五前提交接口文档","action":"跟进张三提交接口文档","deadline":"2026-10-17T18:00:00","importance":"high","reason":"群友明确认领了带截止时间的待办"}

        没有行动项时输出：
        {"category":"study","summary":"群里讨论了 Redis 持久化方案的取舍","action":"","deadline":null,"importance":"medium","reason":"内容是可供后续查阅的知识整理"}
        """;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // 提示词与模型输出都是纯文本数据，不会进入 HTML 上下文，
        // 因此关闭默认的 HTML 转义，避免中文被逐字转成 <summary> 撑大请求体。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    /// <summary>
    /// 把配置里的端点拼成 <c>{Endpoint}/chat/completions</c>。
    /// </summary>
    /// <param name="endpoint">服务基址，例如 <c>https://api.openai.com/v1</c>。</param>
    /// <exception cref="ArgumentException">端点为空、不是绝对 URI、协议不是 http(s)，或非回环地址使用了明文 HTTP。</exception>
    public static Uri BuildChatCompletionsUri(string endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);

        var trimmed = endpoint.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var baseUri))
        {
            throw new ArgumentException($"模型端点 '{trimmed}' 不是合法的绝对 URI。", nameof(endpoint));
        }

        if (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"模型端点协议必须是 http 或 https，当前为 '{baseUri.Scheme}'。", nameof(endpoint));
        }

        // 非回环地址强制 HTTPS：明文 HTTP 在局域网内可能被嗅探，API Key 会泄露。
        // 回环地址（localhost、127.0.0.0/8、::1）允许 HTTP，方便本地推理服务开发调试。
        if (baseUri.Scheme == Uri.UriSchemeHttp && !EndpointTransportPolicy.IsLoopback(baseUri.Host))
        {
            throw new ArgumentException(
                $"模型端点 '{trimmed}' 使用明文 HTTP 且目标不是回环地址，存在凭据泄露风险。" +
                "请改用 HTTPS，或将端点指向 localhost / 127.0.0.1 等回环地址。",
                nameof(endpoint));
        }

        var builder = new UriBuilder(baseUri)
        {
            // 端点里若带查询串或片段，拼接路径后会失效，这里直接清掉。
            Query = string.Empty,
            Fragment = string.Empty,
        };

        var path = builder.Path.TrimEnd('/');

        // 已经指向具体接口时不重复追加，容忍用户直接填完整的 completions 地址。
        if (!path.EndsWith(ChatCompletionsPath, StringComparison.OrdinalIgnoreCase))
        {
            path += ChatCompletionsPath;
        }

        builder.Path = path;
        return builder.Uri;
    }

    /// <summary>
    /// 生成可安全落盘与写日志的端点描述：去掉 userinfo、query 与 fragment。
    /// </summary>
    public static string DescribeEndpoint(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        var builder = new UriBuilder(uri.Scheme, uri.Host, uri.Port)
        {
            Path = uri.AbsolutePath,
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty,
        };

        return builder.Uri.GetLeftPart(UriPartial.Path);
    }

    /// <summary>
    /// 把批次渲染成 user 消息正文：群信息、窗口时间，逐条消息按 <c>[时间] 发送者: 正文</c> 排列。
    /// </summary>
    /// <param name="batch">待摘要批次。</param>
    /// <param name="options">截断等参数；不传时用 <see cref="LlmPromptOptions.Default"/>。</param>
    /// <returns>批次文本；没有任何可用正文时返回空串。</returns>
    public static string BuildBatchText(AggregationBatch batch, LlmPromptOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var settings = options ?? LlmPromptOptions.Default;
        var messages = batch.Messages ?? (IReadOnlyList<Message>)Array.Empty<Message>();

        var builder = new StringBuilder(512);
        AppendHeader(builder, batch);
        builder.Append("消息数：").Append(messages.Count).Append('\n');
        AppendMessageList(builder, messages, settings.MaxBatchChars);

        return builder.ToString();
    }

    /// <summary>
    /// 判断批次里是否至少有一条带正文的消息。
    /// 全是空消息时没有可摘要的内容，调用方应据此跳过请求而不是白发一次调用。
    /// </summary>
    public static bool HasSummarizableContent(AggregationBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var messages = batch.Messages;
        if (messages is null || messages.Count == 0)
        {
            return false;
        }

        foreach (var message in messages)
        {
            if (!string.IsNullOrWhiteSpace(message?.Text))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 组装 <c>/chat/completions</c> 的 UTF-8 请求体。
    /// </summary>
    /// <param name="model">模型名。</param>
    /// <param name="systemPrompt">system 消息正文。</param>
    /// <param name="batchText">user 消息正文。</param>
    /// <param name="options">可调参数。</param>
    public static byte[] BuildRequestBody(
        string model,
        string systemPrompt,
        string batchText,
        LlmPromptOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model, nameof(model));
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(batchText);

        var settings = options ?? LlmPromptOptions.Default;
        var buffer = new ArrayBufferWriter<byte>(EstimateSize(systemPrompt, batchText));

        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("model", model);
            writer.WriteBoolean("stream", false);
            writer.WriteNumber("temperature", settings.Temperature);

            if (settings.MaxTokens > 0)
            {
                writer.WriteNumber("max_tokens", settings.MaxTokens);
            }

            writer.WriteStartArray("messages");
            WriteMessage(writer, "system", systemPrompt);
            WriteMessage(writer, "user", batchText);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>提示词字符数（system + user），用于 <c>llm_calls.prompt_chars</c>。</summary>
    public static int MeasurePromptChars(string systemPrompt, string batchText)
    {
        ArgumentNullException.ThrowIfNull(systemPrompt);
        ArgumentNullException.ThrowIfNull(batchText);

        return systemPrompt.Length + batchText.Length;
    }

    /// <summary>
    /// 去掉模型输出常见的 <c>```json … ```</c> 包裹与首尾空白，便于直接按 JSON 解析。
    /// </summary>
    public static string StripCodeFence(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var text = content.Trim();
        if (text.Length == 0 || text[0] != '`')
        {
            return text;
        }

        var firstNewLine = text.IndexOf('\n', StringComparison.Ordinal);
        if (firstNewLine < 0)
        {
            return text;
        }

        var closing = text.LastIndexOf("```", StringComparison.Ordinal);
        if (closing <= firstNewLine)
        {
            return text;
        }

        return text[firstNewLine..closing].Trim();
    }

    private static void AppendHeader(StringBuilder builder, AggregationBatch batch)
    {
        var groupName = string.IsNullOrWhiteSpace(batch.GroupName) ? "（未命名群）" : batch.GroupName;
        builder.Append("群名：").Append(groupName)
            .Append("（群号 ").Append(batch.GroupId).Append("）\n");

        builder.Append("时间窗口：")
            .Append(FormatTimestamp(batch.WindowStart))
            .Append(" ~ ")
            .Append(FormatTimestamp(batch.WindowEnd))
            .Append('\n');
    }

    private static void AppendMessageList(StringBuilder builder, IReadOnlyList<Message> messages, int maxChars)
    {
        var limit = maxChars > 0 ? maxChars : int.MaxValue;
        var used = 0;
        var truncated = false;

        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var text = (message?.Text ?? string.Empty).Trim();

            if (text.Length == 0)
            {
                continue;
            }

            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"[{FormatTimestamp(message!.Timestamp)}] {message.Sender}: {text}\n");

            if (used + line.Length > limit)
            {
                truncated = true;
                break;
            }

            builder.Append(line);
            used += line.Length;
        }

        if (truncated)
        {
            builder.Append("（正文过长，以上内容已截断）\n");
        }
    }

    private static void WriteMessage(Utf8JsonWriter writer, string role, string content)
    {
        writer.WriteStartObject();
        writer.WriteString("role", role);
        writer.WriteString("content", content);
        writer.WriteEndObject();
    }

    private static string FormatTimestamp(DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static int EstimateSize(string systemPrompt, string batchText) =>
        Encoding.UTF8.GetByteCount(systemPrompt) + Encoding.UTF8.GetByteCount(batchText) + 256;
}

/// <summary>
/// 解析 OpenAI 兼容 <c>/chat/completions</c> 的响应外壳，取出模型正文。
/// </summary>
/// <remarks>
/// 面向"OpenAI 兼容"而非"OpenAI 本身"：<c>content</c> 既可能是字符串，也可能是
/// <c>[{ "type": "text", "text": … }]</c> 的分段数组；两种都要认。
/// <para>
/// 这里只管"把正文从信封里掏出来"。正文本身是否符合摘要契约，由
/// <see cref="LlmResultMapper.MapOrFallback"/> 负责——校验不通过会回退到本地规则，
/// 而不是在这里被判成一次失败的调用。
/// </para>
/// </remarks>
internal static class LlmResponseParser
{
    /// <summary>
    /// 从响应 JSON 中取出模型正文。
    /// </summary>
    /// <param name="payload">响应体原文。</param>
    /// <param name="content">取到的正文；失败时为空串。</param>
    /// <param name="reason">失败原因；成功时为 <see langword="null"/>。</param>
    internal static bool TryExtractContent(string payload, out string content, out string? reason)
    {
        content = string.Empty;
        reason = null;

        if (string.IsNullOrWhiteSpace(payload))
        {
            reason = "响应体为空。";
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            reason = $"响应体不是合法 JSON：{ex.Message}";
            return false;
        }

        using (document)
        {
            var root = document.RootElement;

            // 有的实现把错误放在 200 响应体里（例如 {"error": {...}}），这里提前识别出来。
            if (root.TryGetProperty("error", out var error))
            {
                reason = $"响应体携带 error 字段：{ReadErrorMessage(error)}";
                return false;
            }

            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array
                || choices.GetArrayLength() == 0)
            {
                reason = "响应体缺少非空的 choices 数组。";
                return false;
            }

            var first = choices[0];
            if (!first.TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var rawContent))
            {
                reason = "choices[0].message.content 不存在。";
                return false;
            }

            content = ReadContent(rawContent);
            if (content.Length == 0)
            {
                reason = "choices[0].message.content 为空。";
                return false;
            }

            return true;
        }
    }

    /// <summary>
    /// 读取 content：字符串直接用；分段数组则把 <c>text</c> 段按顺序拼接。
    /// </summary>
    private static string ReadContent(JsonElement content)
    {
        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return content.GetString() ?? string.Empty;

            case JsonValueKind.Array:
                var builder = new StringBuilder();
                foreach (var part in content.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(part.GetString());
                        continue;
                    }

                    if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        builder.Append(text.GetString());
                    }
                }

                return builder.ToString().Trim();

            default:
                return string.Empty;
        }
    }

    private static string ReadErrorMessage(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.String)
        {
            return error.GetString() ?? "未提供说明。";
        }

        if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var message))
        {
            return message.GetString() ?? "未提供说明。";
        }

        return "未提供说明。";
    }
}