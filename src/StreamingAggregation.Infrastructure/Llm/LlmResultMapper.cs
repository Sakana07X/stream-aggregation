using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Diagnostics;
using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Infrastructure.Llm;

/// <summary>
/// 一次聚合的产出：一个摘要，外加可选的行动项。
/// </summary>
public sealed record DigestMapping
{
    /// <summary>摘要（始终存在：模型输出不合格时由本地规则补上）。</summary>
    public required Digest Digest { get; init; }

    /// <summary>行动项；模型或规则都没认定需要行动时为 null。</summary>
    public ActionItem? ActionItem { get; init; }

    /// <summary>是否走了本地规则回退。</summary>
    public bool UsedFallback { get; init; }

    /// <summary>触发回退的校验失败明细；正常路径为空。</summary>
    public IReadOnlyList<DigestValidationError> ValidationErrors { get; init; } =
        Array.Empty<DigestValidationError>();
}

/// <summary>
/// 把模型输出映射成领域对象，并在校验不通过时改用 <see cref="FallbackDigestBuilder"/>。
/// </summary>
/// <remarks>
/// <para>
/// 标识符规则：模型路径 <c>llm-d-{batchId}</c> / <c>llm-a-{batchId}</c>，
/// 回退路径 <c>fb-d-{batchId}</c> / <c>fb-a-{batchId}</c>。
/// 都是按批次确定性的，同一批次重跑不会产生重复记录。
/// </para>
/// <para>
/// 创建时间与相对日期换算走注入的 <see cref="IClock"/>。
/// </para>
/// </remarks>
public sealed class LlmResultMapper
{
    private const string DigestIdPrefix = "llm-d-";
    private const string ActionIdPrefix = "llm-a-";

    private readonly IClock _clock;
    private readonly FallbackDigestBuilder _fallbackBuilder;

    /// <param name="clock">时间源。</param>
    public LlmResultMapper(IClock clock)
        : this(clock, fallbackBuilder: null)
    {
    }

    /// <param name="clock">时间源。</param>
    /// <param name="fallbackBuilder">回退摘要构造器；为 null 时按同一时钟新建一个。</param>
    public LlmResultMapper(IClock clock, FallbackDigestBuilder? fallbackBuilder)
    {
        Guard.NotNull(clock);

        _clock = clock;
        _fallbackBuilder = fallbackBuilder ?? new FallbackDigestBuilder(clock);
    }

    /// <summary>
    /// 入口：校验模型原始输出，通过就映射，不通过就回退到本地规则。
    /// 任何输入都有可用结果，不会抛异常。
    /// </summary>
    /// <param name="batch">聚合批次，提供批次 ID、群组 ID。</param>
    /// <param name="rawJson">模型原始 JSON 输出。</param>
    public DigestMapping MapOrFallback(AggregationBatch batch, string? rawJson)
    {
        Guard.NotNull(batch);

        var validation = DigestSchemaValidator.Validate(rawJson);
        if (validation.IsValid)
        {
            return Map(batch, validation.Draft!);
        }

        var fallback = _fallbackBuilder.Build(batch);
        return fallback with { ValidationErrors = validation.Errors };
    }

    /// <summary>
    /// 把已通过校验的草稿映射成 <see cref="Digest"/> 与可选 <see cref="ActionItem"/>。
    /// </summary>
    /// <param name="batch">聚合批次，提供批次 ID、群组 ID。</param>
    /// <param name="draft">校验通过的模型输出。</param>
    public DigestMapping Map(AggregationBatch batch, ValidatedDigestDraft draft)
    {
        Guard.NotNull(batch);
        Guard.NotNull(draft);
        Guard.NotNullOrWhiteSpace(draft.Summary, nameof(draft));

        var digest = new Digest
        {
            Id = DigestIdPrefix + batch.Id,
            BatchId = batch.Id,
            Category = draft.Category,
            Summary = draft.Summary,
            Reason = draft.Reason,
            CreatedAt = _clock.Now
        };

        return new DigestMapping
        {
            Digest = digest,
            ActionItem = MapActionItem(batch, draft)
        };
    }

    private static ActionItem? MapActionItem(AggregationBatch batch, ValidatedDigestDraft draft)
    {
        if (!draft.HasActionItem)
        {
            return null;
        }

        return new ActionItem
        {
            Id = ActionIdPrefix + batch.Id,
            Action = draft.Action!,
            // 模型给的截止时间可能解析不出来，校验层已降级为 null，这里原样承接。
            Deadline = draft.Deadline,
            Importance = draft.Importance,
            Reason = draft.Reason,
            SourceGroupId = batch.GroupId
        };
    }
}