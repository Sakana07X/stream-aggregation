using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;
using StreamingAggregation.Core;

namespace StreamingAggregation.Infrastructure.Llm;

/// <summary>
/// 模型摘要 JSON 的校验失败原因。
/// </summary>
public enum DigestValidationErrorKind
{
    /// <summary>整体不是合法 JSON（空响应、响应被截断等）。</summary>
    MalformedJson,

    /// <summary>JSON 合法但根节点不是对象。</summary>
    NotAnObject,

    /// <summary>缺少必填字段。</summary>
    FieldMissing,

    /// <summary>字段存在但 JSON 类型不对（例如该给字符串的位置给了数字）。</summary>
    TypeMismatch,

    /// <summary>必填文本字段为空或只有空白。</summary>
    EmptyValue,

    /// <summary>枚举字段不是已定义的取值。</summary>
    InvalidEnum,

    /// <summary>文本字段超出长度上限。</summary>
    TooLong,
}

/// <summary>
/// 单条校验失败明细，字段名与方法名对齐，便于直接写进模型调用记录。
/// </summary>
/// <param name="Field">出问题的字段名；整体性问题用 <c>(root)</c>。</param>
/// <param name="Kind">失败原因分类。</param>
/// <param name="Message">面向人的中文说明。</param>
public sealed record DigestValidationError(string Field, DigestValidationErrorKind Kind, string Message)
{
    /// <summary>输出成单行诊断文本。</summary>
    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"[{Field}] {Kind}：{Message}");
}

/// <summary>
/// 通过校验的模型输出。字段值已完成规范化（去空白、日期转 UTC），
/// 可直接交给 <see cref="LlmResultMapper"/> 映射成领域对象。
/// </summary>
public sealed record ValidatedDigestDraft
{
    /// <summary>摘要分类。</summary>
    public required DigestCategory Category { get; init; }

    /// <summary>摘要正文（已去首尾空白，非空）。</summary>
    public required string Summary { get; init; }

    /// <summary>归类理由（已去首尾空白，非空）。</summary>
    public required string Reason { get; init; }

    /// <summary>动作描述；为 null 或空白表示本轮没有行动项。</summary>
    public string? Action { get; init; }

    /// <summary>截止时间（UTC）；未提供或无法解析时为 null。</summary>
    public DateTime? Deadline { get; init; }

    /// <summary>重要程度；仅在 <see cref="HasActionItem"/> 为 true 时使用。缺失时降级为 <see cref="Importance.Medium"/>。</summary>
    public required Importance Importance { get; init; }

    /// <summary>是否要生成行动项。</summary>
    public bool HasActionItem => !string.IsNullOrWhiteSpace(Action);
}

/// <summary>
/// 校验结果：要么带一个可用草稿，要么带一组失败明细，二者互斥。
/// </summary>
public sealed class DigestValidationResult
{
    private DigestValidationResult(
        ValidatedDigestDraft? draft,
        IReadOnlyList<DigestValidationError> errors)
    {
        Draft = draft;
        Errors = errors;
    }

    /// <summary>校验是否通过。</summary>
    public bool IsValid => Draft is not null;

    /// <summary>通过时的草稿；失败时为 null。</summary>
    public ValidatedDigestDraft? Draft { get; }

    /// <summary>全部失败明细（通过时为空）。</summary>
    public IReadOnlyList<DigestValidationError> Errors { get; }

    /// <summary>校验通过。</summary>
    public static DigestValidationResult Success(ValidatedDigestDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new DigestValidationResult(draft, Array.Empty<DigestValidationError>());
    }

    /// <summary>校验失败，至少带一条明细。</summary>
    public static DigestValidationResult Failure(IReadOnlyList<DigestValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count == 0)
        {
            throw new ArgumentException("失败结果至少要有一条错误明细。", nameof(errors));
        }

        return new DigestValidationResult(null, errors);
    }

    /// <summary>把失败明细拼成一行，便于写日志或落库。</summary>
    public string Describe()
        => IsValid ? "ok" : string.Join("; ", Errors.Select(error => error.ToString()));
}

/// <summary>
/// 校验模型返回的摘要 JSON。
/// </summary>
/// <remarks>
/// <para>
/// 契约：根节点是对象，必须同时包含
/// <c>category</c>、<c>summary</c>、<c>action</c>、<c>deadline</c>、
/// <c>importance</c>、<c>reason</c> 六个字段。字段名大小写不敏感，多余字段忽略。
/// 其中 <c>importance</c> 是软字段：整个字段缺失也算合法（见下）。
/// </para>
/// <para>
/// 判失败的四种情况：整体不是合法 JSON、字段缺失、字段 JSON 类型不对、枚举取值非法；
/// 另有"必填文本为空"与"文本超长"同样判失败。
/// </para>
/// <para>
/// 两处降级，都不丢弃整条摘要：
/// <list type="bullet">
/// <item><description>
/// <c>deadline</c>：<c>null</c>、空串或无法解析的日期串都按"没有截止时间"处理。
/// </description></item>
/// <item><description>
/// <c>importance</c>：字段缺失、<c>null</c>、空串或无法识别的枚举名都按
/// <see cref="DefaultImportance"/> 处理。重要度是"锦上添花"的排序键，
/// 猜错一次不该让摘要与行动项一起作废。
/// </description></item>
/// </list>
/// </para>
/// <para>
/// 降级只覆盖"信息缺失或读不懂"，不覆盖"契约写错了"：JSON 类型不对
/// （例如 <c>importance</c> 给了数字）仍然判失败，那是模型的硬错误，值得看到。
/// </para>
/// <para>
/// 无状态、无 I/O，不依赖 <see cref="Core.Abstractions.IClock"/>。
/// </para>
/// </remarks>
public static class DigestSchemaValidator
{
    /// <summary>摘要正文长度上限，超出判失败。</summary>
    public const int MaxSummaryLength = 1000;

    /// <summary>归类理由长度上限，超出判失败。</summary>
    public const int MaxReasonLength = 1000;

    /// <summary>动作描述长度上限，超出判失败。</summary>
    public const int MaxActionLength = 500;

    /// <summary>
    /// <c>importance</c> 缺失或读不懂时的兜底取值。
    /// 选 <see cref="Importance.Medium"/>：它对"随手扫一眼"和"尽快跟进"都不算错。
    /// </summary>
    public const Importance DefaultImportance = Importance.Medium;

    private const string RootField = "(root)";

    private const string CategoryField = "category";
    private const string SummaryField = "summary";
    private const string ActionField = "action";
    private const string DeadlineField = "deadline";
    private const string ImportanceField = "importance";
    private const string ReasonField = "reason";

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 16
    };

    // 未带时区信息的日期串按 UTC 解释：与 SQLite 落库"一律 ISO-8601 UTC"的约定一致。
    private const DateTimeStyles DeadlineStyles =
        DateTimeStyles.AllowWhiteSpaces
        | DateTimeStyles.AssumeUniversal
        | DateTimeStyles.AdjustToUniversal;

    private static readonly string[] DeadlineFormats =
    [
        // K 覆盖尾部时区：Z 或 +08:00 都要认，否则带偏移的合法日期会被误判成"解析失败"。
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-ddTHH:mmK",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd",
        "yyyy/MM/dd HH:mm:ss",
        "yyyy/MM/dd HH:mm",
        "yyyy/MM/dd",
        "yyyy年M月d日 HH:mm",
        "yyyy年M月d日"
    ];

    /// <summary>
    /// 校验模型原始输出，返回草稿或失败明细。
    /// 任何输入（包括 null、空串、非 JSON 文本）都有返回值，不抛异常。
    /// </summary>
    public static DigestValidationResult Validate(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return DigestValidationResult.Failure(
            [
                new DigestValidationError(RootField, DigestValidationErrorKind.MalformedJson, "模型返回为空。")
            ]);
        }

        if (!TryParseDocument(rawJson, out var document, out var parseError))
        {
            return DigestValidationResult.Failure(
            [
                new DigestValidationError(RootField, DigestValidationErrorKind.MalformedJson, parseError)
            ]);
        }

        using (document)
        {
            return ValidateRoot(document!.RootElement);
        }
    }

    /// <summary>合法枚举取值清单，写进提示词与错误提示，避免模型反复踩同一个坑。</summary>
    public static string DescribeAllowedCategories() => EnumNameMap<DigestCategory>.Allowed;

    /// <summary>合法重要度清单；缺失或无法识别时按 <see cref="DefaultImportance"/> 处理。</summary>
    public static string DescribeAllowedImportances() => EnumNameMap<Importance>.Allowed;

    private static DigestValidationResult ValidateRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return DigestValidationResult.Failure(
            [
                new DigestValidationError(
                    RootField,
                    DigestValidationErrorKind.NotAnObject,
                    $"期望 JSON 对象，得到 {Describe(root.ValueKind)}。")
            ]);
        }

        var errors = new List<DigestValidationError>();
        var category = ReadEnum<DigestCategory>(root, CategoryField, errors);
        var importance = ReadImportance(root, errors);
        var summary = ReadRequiredText(root, SummaryField, MaxSummaryLength, errors);
        var reason = ReadRequiredText(root, ReasonField, MaxReasonLength, errors);
        var action = ReadOptionalText(root, ActionField, MaxActionLength, errors);
        var deadline = ReadDeadline(root, errors);

        if (errors.Count > 0)
        {
            return DigestValidationResult.Failure(errors);
        }

        return DigestValidationResult.Success(new ValidatedDigestDraft
        {
            Category = category!.Value,
            Summary = summary!,
            Reason = reason!,
            Action = action,
            Deadline = deadline,
            Importance = importance
        });
    }

    private static bool TryParseDocument(string rawJson, out JsonDocument? document, out string error)
    {
        try
        {
            document = JsonDocument.Parse(Unwrap(rawJson), DocumentOptions);
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            document = null;
            error = $"非法 JSON：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 剥掉模型常见的 ```json 围栏，并把前后缀说明文字裁掉，只留最外层花括号。
    /// 模型经常在 JSON 前后加解释或围栏，这些不该让整轮结果作废。
    /// </summary>
    private static string Unwrap(string rawJson)
    {
        var text = rawJson.Trim();

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var headerEnd = text.IndexOf('\n');
            if (headerEnd >= 0)
            {
                text = text[(headerEnd + 1)..].Trim();
            }

            var fenceEnd = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0)
            {
                text = text[..fenceEnd].Trim();
            }
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        return start >= 0 && end > start ? text[start..(end + 1)] : text;
    }

    private static TEnum? ReadEnum<TEnum>(JsonElement root, string field, List<DigestValidationError> errors)
        where TEnum : struct, Enum
    {
        if (!TryGetField(root, field, out var element, errors))
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            errors.Add(TypeMismatch(field, $"期望字符串，得到 {Describe(element.ValueKind)}。"));
            return null;
        }

        var text = element.GetString()?.Trim() ?? string.Empty;
        if (EnumNameMap<TEnum>.Map.TryGetValue(text, out var value))
        {
            return value;
        }

        errors.Add(new DigestValidationError(
            field,
            DigestValidationErrorKind.InvalidEnum,
            $"取值「{text}」不在允许范围：{EnumNameMap<TEnum>.Allowed}。"));
        return null;
    }

    /// <summary>
    /// 读取 <c>importance</c>，缺失或读不懂时降级为 <see cref="DefaultImportance"/>，不报错。
    /// </summary>
    /// <remarks>
    /// 与 <c>deadline</c> 同一思路：重要度只是行动项的排序键，
    /// 模型漏写或写了个 "critical"，不该把已经合格的摘要和动作一起丢掉。
    /// 但 JSON 类型不对仍判失败——那是契约写错，不是信息缺失，值得被看见。
    /// </remarks>
    private static Importance ReadImportance(JsonElement root, List<DigestValidationError> errors)
    {
        if (!TryFindField(root, ImportanceField, out var element))
        {
            return DefaultImportance;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return DefaultImportance;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            errors.Add(TypeMismatch(ImportanceField, $"期望字符串或 null，得到 {Describe(element.ValueKind)}。"));
            return DefaultImportance;
        }

        var text = element.GetString()?.Trim() ?? string.Empty;
        return EnumNameMap<Importance>.Map.TryGetValue(text, out var value) ? value : DefaultImportance;
    }

    private static string? ReadRequiredText(
        JsonElement root,
        string field,
        int maxLength,
        List<DigestValidationError> errors)
    {
        if (!TryGetField(root, field, out var element, errors))
        {
            return null;
        }

        var text = ReadText(element, field, errors);
        if (text is null)
        {
            return null;
        }

        if (text.Length == 0)
        {
            errors.Add(new DigestValidationError(
                field,
                DigestValidationErrorKind.EmptyValue,
                "必填文本为空。"));
            return null;
        }

        return CheckLength(field, text, maxLength, errors);
    }

    private static string? ReadOptionalText(
        JsonElement root,
        string field,
        int maxLength,
        List<DigestValidationError> errors)
    {
        if (!TryGetField(root, field, out var element, errors))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var text = ReadText(element, field, errors);

        // action 为空白表示"没有行动项"，与 null 同义，不算失败。
        return text is null || text.Length == 0 ? null : CheckLength(field, text, maxLength, errors);
    }

    private static string? ReadText(JsonElement element, string field, List<DigestValidationError> errors)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            errors.Add(TypeMismatch(field, $"期望字符串，得到 {Describe(element.ValueKind)}。"));
            return null;
        }

        return element.GetString()?.Trim();
    }

    private static string? CheckLength(
        string field,
        string text,
        int maxLength,
        List<DigestValidationError> errors)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        errors.Add(new DigestValidationError(
            field,
            DigestValidationErrorKind.TooLong,
            $"长度 {text.Length} 超过上限 {maxLength}。"));
        return null;
    }

    private static DateTime? ReadDeadline(JsonElement root, List<DigestValidationError> errors)
    {
        if (!TryGetField(root, DeadlineField, out var element, errors))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            errors.Add(TypeMismatch(DeadlineField, $"期望字符串或 null，得到 {Describe(element.ValueKind)}。"));
            return null;
        }

        // 唯一允许的降级：格式不认识就当没有截止时间，摘要与行动项照常可用。
        return ParseDeadline(element.GetString());
    }

    private static DateTime? ParseDeadline(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // 用 InvariantCulture 解析：模型给的是 ISO/公历日期，不该受本机区域设置影响。
        return DateTime.TryParseExact(
            text.Trim(),
            DeadlineFormats,
            CultureInfo.InvariantCulture,
            DeadlineStyles,
            out var parsed)
            ? parsed
            : null;
    }

    private static bool TryGetField(
        JsonElement root,
        string field,
        out JsonElement element,
        List<DigestValidationError> errors)
    {
        if (TryFindField(root, field, out element))
        {
            return true;
        }

        errors.Add(new DigestValidationError(
            field,
            DigestValidationErrorKind.FieldMissing,
            $"缺少必填字段「{field}」。"));
        return false;
    }

    /// <summary>
    /// 找字段但不记账，供可选字段使用：找不到只是"没有值"，不是契约错误。
    /// </summary>
    private static bool TryFindField(JsonElement root, string field, out JsonElement element)
    {
        // 字段名大小写不敏感：模型写 "Category" 和 "category" 都接受，取先出现的那个。
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, field, StringComparison.OrdinalIgnoreCase))
            {
                element = property.Value;
                return true;
            }
        }

        element = default;
        return false;
    }

    private static DigestValidationError TypeMismatch(string field, string message)
        => new(field, DigestValidationErrorKind.TypeMismatch, message);

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "对象",
        JsonValueKind.Array => "数组",
        JsonValueKind.String => "字符串",
        JsonValueKind.Number => "数字",
        JsonValueKind.True or JsonValueKind.False => "布尔值",
        JsonValueKind.Null => "null",
        _ => "undefined"
    };

    /// <summary>
    /// 枚举名到取值的映射：只认已定义的名字（大小写不敏感）。
    /// 刻意不走 <c>Enum.TryParse</c>，否则 "3" 这类数字串会被当成合法取值。
    /// </summary>
    private static class EnumNameMap<TEnum>
        where TEnum : struct, Enum
    {
        internal static readonly FrozenDictionary<string, TEnum> Map =
            Enum.GetNames<TEnum>()
                .ToFrozenDictionary(name => name, name => Enum.Parse<TEnum>(name), StringComparer.OrdinalIgnoreCase);

        internal static string Allowed { get; } = string.Join(" / ", Enum.GetNames<TEnum>());
    }
}