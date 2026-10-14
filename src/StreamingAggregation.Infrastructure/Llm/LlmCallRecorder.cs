using Microsoft.Data.Sqlite;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Infrastructure.Persistence;

namespace StreamingAggregation.Infrastructure.Llm;

/// <summary>LLM 相关诊断的日志级别，与 <c>OneBotDiagnosticLevel</c> 保持同一套语义。</summary>
public enum LlmDiagnosticLevel
{
    /// <summary>调试信息。</summary>
    Debug,

    /// <summary>常规信息。</summary>
    Info,

    /// <summary>警告。</summary>
    Warning,

    /// <summary>错误。</summary>
    Error,
}

/// <summary>一条已脱敏的 LLM 诊断记录。</summary>
/// <param name="Level">日志级别。</param>
/// <param name="Message">日志正文；由写入方保证不含 API Key。</param>
/// <param name="Exception">关联异常，可为 <see langword="null"/>。</param>
public sealed record LlmDiagnostic(LlmDiagnosticLevel Level, string Message, Exception? Exception = null);

/// <summary>
/// 模型调用失败的归一化分类。
/// </summary>
/// <remarks>
/// 所有失败路径（超时、网络、HTTP 非 2xx、非 JSON 返回、配置缺失）都落到本枚举之一，
/// 调用方据此区分"稍后重试"与"需要用户改配置"。
/// </remarks>
public enum LlmFailureKind
{
    /// <summary>调用成功。</summary>
    None,

    /// <summary>配置缺失或非法：端点/模型名为空、端点不是合法的 http(s) 绝对地址等。</summary>
    ConfigurationInvalid,

    /// <summary>密钥库中没有取到 API Key。</summary>
    MissingApiKey,

    /// <summary>批次里没有任何可用文本，未发出请求。</summary>
    EmptyBatch,

    /// <summary>超过 <c>ModelSettings.TimeoutSeconds</c>。</summary>
    Timeout,

    /// <summary>网络层失败：DNS、连接被拒、连接重置、TLS 握手失败等。</summary>
    Network,

    /// <summary>服务端返回了非 2xx 状态码。</summary>
    HttpStatus,

    /// <summary>响应不是预期的 JSON，或模型输出无法解析成摘要。</summary>
    InvalidResponse,

    /// <summary>兜底：其它未预期异常，已记录到诊断日志。</summary>
    Unexpected,
}

/// <summary>
/// 一次模型调用的记录，对应 <c>llm_calls</c> 表的一行。
/// </summary>
public sealed record LlmCallRecord
{
    /// <summary>写入 <c>llm_calls.id</c> 的主键。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>来源批次；可空（批次被清理后置空）。</summary>
    public string? BatchId { get; init; }

    /// <summary>调用的模型名。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>调用端点（已去掉 query 与 userinfo，避免凭据随记录落盘）。</summary>
    public string Endpoint { get; init; } = string.Empty;

    /// <summary>是否调用成功。</summary>
    public bool Success { get; init; }

    /// <summary>失败分类；成功时为 <see cref="LlmFailureKind.None"/>。</summary>
    public LlmFailureKind FailureKind { get; init; } = LlmFailureKind.None;

    /// <summary>失败原因；成功时为 <see langword="null"/>。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>提示词字符数（system prompt + 批次文本）。</summary>
    public int PromptChars { get; init; }

    /// <summary>模型输出字符数。</summary>
    public int CompletionChars { get; init; }

    /// <summary>调用耗时（毫秒）。</summary>
    public long DurationMs { get; init; }

    /// <summary>记录时间（UTC）。</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>构造一条成功记录。</summary>
    public static LlmCallRecord Succeeded(
        string? batchId,
        string model,
        string endpoint,
        int promptChars,
        int completionChars,
        TimeSpan duration,
        DateTime createdAt) => new()
        {
            Id = NewId(),
            BatchId = batchId,
            Model = model,
            Endpoint = endpoint,
            Success = true,
            FailureKind = LlmFailureKind.None,
            ErrorMessage = null,
            PromptChars = promptChars,
            CompletionChars = completionChars,
            DurationMs = ToMilliseconds(duration),
            CreatedAt = createdAt,
        };

    /// <summary>构造一条失败记录；失败原因会带上分类前缀，便于在 <c>llm_calls</c> 里直接读懂。</summary>
    public static LlmCallRecord Failed(
        string? batchId,
        string model,
        string endpoint,
        LlmFailureKind kind,
        string message,
        int promptChars,
        int completionChars,
        TimeSpan duration,
        DateTime createdAt) => new()
        {
            Id = NewId(),
            BatchId = batchId,
            Model = model,
            Endpoint = endpoint,
            Success = false,
            FailureKind = kind,
            ErrorMessage = ComposeError(kind, message),
            PromptChars = promptChars,
            CompletionChars = completionChars,
            DurationMs = ToMilliseconds(duration),
            CreatedAt = createdAt,
        };

    /// <summary>
    /// 供 <see cref="ISummarizationClient"/> 之外的使用方读取的失败摘要；
    /// 成功时返回 <see langword="null"/>。内容与写入数据库的 <c>error_message</c> 一致。
    /// </summary>
    public string? DescribeError() => Success ? null : ErrorMessage;

    /// <summary>把失败分类拼成中文标签。</summary>
    public static string LabelOf(LlmFailureKind kind) => kind switch
    {
        LlmFailureKind.None => "成功",
        LlmFailureKind.ConfigurationInvalid => "配置错误",
        LlmFailureKind.MissingApiKey => "缺少 API Key",
        LlmFailureKind.EmptyBatch => "批次无可用文本",
        LlmFailureKind.Timeout => "调用超时",
        LlmFailureKind.Network => "网络失败",
        LlmFailureKind.HttpStatus => "服务端返回错误状态",
        LlmFailureKind.InvalidResponse => "响应无法解析",
        LlmFailureKind.Unexpected => "未预期错误",
        _ => "未知错误",
    };

    /// <summary>错误正文上限，避免把整页错误页面写进数据库。</summary>
    public const int MaxErrorMessageChars = 2_000;

    /// <summary>格式化落库的 <c>error_message</c>：<c>分类: 原因</c>，并截断超长内容。</summary>
    internal static string ComposeError(LlmFailureKind kind, string message)
    {
        var text = string.IsNullOrWhiteSpace(message) ? "未提供原因。" : message.Trim();
        var composed = $"{LabelOf(kind)}: {text}";

        return composed.Length <= MaxErrorMessageChars
            ? composed
            : string.Concat(composed.AsSpan(0, MaxErrorMessageChars), "…");
    }

    private static long ToMilliseconds(TimeSpan duration)
    {
        var milliseconds = duration.TotalMilliseconds;

        return double.IsNaN(milliseconds) || milliseconds <= 0 ? 0L : (long)milliseconds;
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    /// <inheritdoc />
    public override string ToString() =>
        $"LlmCallRecord({Model} @ {Endpoint}, 成功={Success}, 类型={FailureKind}, 耗时={DurationMs}ms)";
}

/// <summary>模型调用记录的写入端口，便于测试替换为内存实现。</summary>
public interface ILlmCallRecorder
{
    /// <summary>写入一条调用记录。</summary>
    /// <param name="record">调用记录。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task RecordAsync(LlmCallRecord record, CancellationToken cancellationToken = default);
}

/// <summary>
/// 把模型调用记录写入 <c>llm_calls</c> 表（诊断用，不参与业务判断）。
/// </summary>
/// <remarks>
/// <para>
/// 记录是"尽力而为"的旁路：<see cref="RecordAsync"/> 不会因为写库失败而抛出异常，
/// 因此摘要调用本身的成败不受数据库状态影响。
/// </para>
/// <para>
/// <c>llm_calls.batch_id</c> 上有指向 <c>aggregation_batches</c> 的外键，
/// 对尚未落库的批次写记录会触发约束失败；此时只记录诊断日志，不影响调用结果。
/// </para>
/// </remarks>
public sealed class LlmCallRecorder : ILlmCallRecorder
{
    private const string InsertSql = """
        INSERT INTO llm_calls
            (id, batch_id, model, endpoint, success, error_message,
             prompt_chars, completion_chars, duration_ms, created_at)
        VALUES
            ($id, $batch_id, $model, $endpoint, $success, $error_message,
             $prompt_chars, $completion_chars, $duration_ms, $created_at);
        """;

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IClock _clock;
    private readonly Action<LlmDiagnostic>? _diagnostics;

    /// <param name="connectionFactory">连接工厂。</param>
    /// <param name="clock">时间源；不传时使用 UTC 系统时钟。</param>
    /// <param name="diagnostics">可选诊断回调，收到的是已脱敏的消息。</param>
    public LlmCallRecorder(
        ISqliteConnectionFactory connectionFactory,
        IClock? clock = null,
        Action<LlmDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);

        _connectionFactory = connectionFactory;
        _clock = clock ?? SystemClock.Instance;
        _diagnostics = diagnostics;
    }

    /// <inheritdoc />
    public async Task RecordAsync(LlmCallRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        try
        {
            await InsertAsync(record, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 诊断旁路失败不允许影响调用结果，只留日志。
            _diagnostics?.Invoke(new LlmDiagnostic(
                LlmDiagnosticLevel.Warning,
                $"写入模型调用记录失败：{ex.Message}",
                ex));
        }
    }

    private async Task InsertAsync(LlmCallRecord record, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = InsertSql;
        command
            .Bind("$id", record.Id)
            .Bind("$batch_id", record.BatchId)
            .Bind("$model", record.Model ?? string.Empty)
            .Bind("$endpoint", record.Endpoint ?? string.Empty)
            .Bind("$success", record.Success ? 1 : 0)
            .Bind("$error_message", record.ErrorMessage)
            .Bind("$prompt_chars", record.PromptChars)
            .Bind("$completion_chars", record.CompletionChars)
            .Bind("$duration_ms", record.DurationMs)
            .Bind("$created_at", SqliteTime.ToStorage(record.CreatedAt));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}