using System.Globalization;
using System.Text.RegularExpressions;
using StreamingAggregation.Core;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Diagnostics;
using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Infrastructure.Llm;

/// <summary>
/// 模型不可用或输出无法通过校验时的本地规则摘要。
/// </summary>
/// <remarks>
/// <para>
/// 目标不是替代模型，而是保证"模型挂了客户端照样能用"：
/// 摘要正文取消息片段，分类按关键词，命中待办/紧急信号时额外产出一个行动项。
/// 关键词没命中但文本带日期或时刻时，同样按"有截止时间 = 待办"处理。
/// 全部规则确定性可复现，不做任何网络调用。
/// </para>
/// <para>
/// 标识符按 <c>fb-d-{batchId}</c> / <c>fb-a-{batchId}</c> 生成：
/// 同一批次重试回退得到同一批 ID，不会重复落库。
/// </para>
/// <para>
/// 时间一律走注入的 <see cref="IClock"/>，"明天""3 天后"这类相对表述以它为基准。
/// </para>
/// </remarks>
public sealed class FallbackDigestBuilder
{
    /// <summary>摘要正文里最多摘录几条消息。</summary>
    public const int MaxMessagesInSummary = 3;

    /// <summary>单条消息摘录长度上限。</summary>
    public const int MaxMessageExcerptLength = 60;

    private const string DigestIdPrefix = "fb-d-";
    private const string ActionIdPrefix = "fb-a-";

    // 只给日期不给时刻时，按当天 23:59 收口。
    private static readonly TimeSpan EndOfDay = TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59);

    // 规则按优先级排列：靠前的类别先命中。
    private static readonly (DigestCategory Category, string Label, string[] Keywords)[] Rules =
    [
        (DigestCategory.Urgent, "紧急",
        [
            "紧急", "十万火急", "加急", "尽快", "立刻", "马上", "asap", "急死",
            "故障", "崩了", "挂了", "无法", "超时", "报警", "急救"
        ]),
        (DigestCategory.Todo, "待办",
        [
            "待办", "todo", "记得", "别忘", "别忘了", "需要做", "要完成", "提交",
            "截止", "deadline", "安排", "跟进", "处理一下", "记得要"
        ]),
        (DigestCategory.Notice, "通知",
        [
            "公告", "通知", "停电", "停机", "维护", "放假", "开会", "提醒",
            "请知悉", "已更新", "上线", "变更"
        ]),
        (DigestCategory.Study, "学习",
        [
            "学习", "教程", "资料", "文档", "分享", "笔记", "源码", "论文",
            "科普", "链接", "手册"
        ])
    ];

    private static readonly Regex RelativeDayPattern = new(
        @"(今天|今日|明天|明日|后天|大后天|(\d{1,2})\s*天后)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AbsoluteDatePattern = new(
        @"(?:(\d{4})\s*年)?\s*(\d{1,2})\s*月\s*(\d{1,2})\s*[日号]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ClockTimePattern = new(
        @"(?<!\d)(\d{1,2})\s*[:：]\s*(\d{2})(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ChineseTimePattern = new(
        @"(?<!\d)(\d{1,2})\s*[点時时]\s*(半|(\d{1,2})\s*分)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IClock _clock;

    /// <param name="clock">时间源，用于摘要创建时间与"明天/3 天后"等相对截止时间的换算。</param>
    public FallbackDigestBuilder(IClock clock)
    {
        Guard.NotNull(clock);

        _clock = clock;
    }

    /// <summary>
    /// 为一个批次生成本地回退摘要。批次内没有任何文本消息时也返回摘要，只是内容较短。
    /// </summary>
    /// <param name="batch">聚合批次。</param>
    public DigestMapping Build(AggregationBatch batch)
    {
        Guard.NotNull(batch);

        var texts = CollectTexts(batch.Messages);
        var classification = Classify(texts);

        var digest = new Digest
        {
            Id = DigestIdPrefix + batch.Id,
            BatchId = batch.Id,
            Category = classification.Category,
            Summary = ComposeSummary(texts),
            Reason = ComposeReason(texts, classification),
            CreatedAt = _clock.Now
        };

        return new DigestMapping
        {
            Digest = digest,
            ActionItem = BuildActionItem(batch, texts, classification),
            UsedFallback = true
        };
    }

    private static List<string> CollectTexts(IReadOnlyList<Message>? messages)
    {
        var texts = new List<string>();
        if (messages is null)
        {
            return texts;
        }

        foreach (var message in messages)
        {
            if (message is not null && !string.IsNullOrWhiteSpace(message.Text))
            {
                texts.Add(message.Text.Trim());
            }
        }

        return texts;
    }

    private static Classification Classify(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0)
        {
            return new Classification(DigestCategory.Normal, string.Empty, Array.Empty<string>());
        }

        var joined = string.Join("\n", texts);
        foreach (var rule in Rules)
        {
            var hits = rule.Keywords
                .Where(keyword => joined.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToArray();

            if (hits.Length > 0)
            {
                return new Classification(rule.Category, rule.Label, hits);
            }
        }

        return HasDeadlineSignal(joined)
            ? new Classification(DigestCategory.Todo, "待办", ["截止时间"])
            : new Classification(DigestCategory.Normal, "普通", Array.Empty<string>());
    }

    /// <summary>文本里是否带日期或时刻。关键词没命中时用它兜底判定"这是个有截止时间的待办"。</summary>
    private static bool HasDeadlineSignal(string text)
        => RelativeDayPattern.IsMatch(text)
        || AbsoluteDatePattern.IsMatch(text)
        || ClockTimePattern.IsMatch(text)
        || ChineseTimePattern.IsMatch(text);

    private static string ComposeSummary(IReadOnlyList<string> texts)
    {
        if (texts.Count == 0)
        {
            return "本轮窗口内没有可摘要的文本消息。";
        }

        var excerpts = texts
            .Take(MaxMessagesInSummary)
            .Select(text => Truncate(text, MaxMessageExcerptLength));

        var body = string.Join("；", excerpts);
        var omitted = texts.Count > MaxMessagesInSummary
            ? string.Format(CultureInfo.InvariantCulture, $"（其余 {texts.Count - MaxMessagesInSummary} 条略）")
            : string.Empty;

        return Truncate($"本轮共 {texts.Count} 条有效消息：{body}{omitted}", DigestSchemaValidator.MaxSummaryLength);
    }

    private static string ComposeReason(IReadOnlyList<string> texts, Classification classification)
    {
        var basis = classification.Hits.Count > 0
            ? string.Format(CultureInfo.InvariantCulture, $"命中关键词「{string.Join("、", classification.Hits)}」")
            : "未命中任何关键词";

        return Truncate(
            string.Format(
                CultureInfo.InvariantCulture,
                $"本地规则摘要（模型未产出可用结果）：对 {texts.Count} 条消息按关键字判定为{classification.Label}，{basis}。"),
            DigestSchemaValidator.MaxReasonLength);
    }

    private ActionItem? BuildActionItem(
        AggregationBatch batch,
        IReadOnlyList<string> texts,
        Classification classification)
    {
        // 只有待办/紧急信号才值得建行动项，避免把闲聊也塞进待办列表。
        if (classification.Category is not (DigestCategory.Urgent or DigestCategory.Todo) || texts.Count == 0)
        {
            return null;
        }

        var action = SelectTriggerText(texts, classification.Hits);
        var now = _clock.Now;

        return new ActionItem
        {
            Id = ActionIdPrefix + batch.Id,
            Action = Truncate(action, DigestSchemaValidator.MaxActionLength),
            Deadline = TryExtractDeadline(action, now),
            Importance = classification.Category == DigestCategory.Urgent ? Importance.High : Importance.Medium,
            Reason = string.Format(
                CultureInfo.InvariantCulture,
                $"本地规则摘要：{classification.Label}信号，关键词「{string.Join("、", classification.Hits)}」。"),
            SourceGroupId = batch.GroupId
        };
    }

    private static string SelectTriggerText(IReadOnlyList<string> texts, IReadOnlyList<string> hits)
    {
        foreach (var text in texts)
        {
            if (hits.Any(hit => text.IndexOf(hit, StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return text;
            }
        }

        return texts[0];
    }

    /// <summary>
    /// 从消息文本里抽截止时间。支持今天/明天/后天/N 天后、M 月 D 日、HH:mm 与 N 点半。
    /// 抽不到返回 null；抽到日期没抽到时刻按当天 23:59 收口。
    /// 文本写了明确日期就照抄（哪怕已经过去），只给时刻才按"最近一次还没到"推算。
    /// </summary>
    private static DateTime? TryExtractDeadline(string text, DateTime now)
    {
        var date = ExtractDate(text, now);
        var time = ExtractTime(text);

        if (date is null && time is null)
        {
            return null;
        }

        var day = date ?? NearestUpcomingDay(now.Date, time!.Value, now);
        var clock = time ?? EndOfDay;
        return day + clock;
    }

    /// <summary>
    /// 只给了时刻没给日期时，落到"最近一次还没到的那个日子"，返回的是日期本身（时刻稍后再加）。
    /// </summary>
    private static DateTime NearestUpcomingDay(DateTime today, TimeSpan timeOfDay, DateTime now)
        => today + timeOfDay <= now ? today.AddDays(1) : today;

    private static DateTime? ExtractDate(string text, DateTime now)
    {
        var relative = RelativeDayPattern.Match(text);
        if (relative.Success)
        {
            var days = relative.Groups[2].Success
                ? ParseInt(relative.Groups[2].Value)
                : RelativeDayOffset(relative.Groups[1].Value);

            return days is null ? null : now.Date.AddDays(days.Value);
        }

        var absolute = AbsoluteDatePattern.Match(text);
        if (!absolute.Success)
        {
            return null;
        }

        return BuildAbsoluteDate(absolute, now);
    }

    private static int? RelativeDayOffset(string keyword) => keyword switch
    {
        "今天" or "今日" => 0,
        "明天" or "明日" => 1,
        "后天" => 2,
        "大后天" => 3,
        _ => null
    };

    private static DateTime? BuildAbsoluteDate(Match match, DateTime now)
    {
        var hasYear = match.Groups[1].Success;
        var year = hasYear ? ParseInt(match.Groups[1].Value) : now.Year;
        var month = ParseInt(match.Groups[2].Value);
        var day = ParseInt(match.Groups[3].Value);

        if (year is null or <= 0 || month is null or <= 0 || day is null or <= 0)
        {
            return null;
        }

        // 交给 TryParseExact 兜住 2 月 30 日这类不存在的日期。
        var candidate = string.Format(
            CultureInfo.InvariantCulture,
            $"{year.Value:D4}-{month.Value:D2}-{day.Value:D2}");

        if (!DateTime.TryParseExact(candidate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return null;
        }

        // 只写了月日且当年那天已过，按明年算。
        return !hasYear && parsed.Date < now.Date ? parsed.AddYears(1) : parsed;
    }

    private static TimeSpan? ExtractTime(string text)
    {
        var clock = ClockTimePattern.Match(text);
        if (clock.Success)
        {
            var hour = ParseInt(clock.Groups[1].Value);
            var minute = ParseInt(clock.Groups[2].Value);
            if (TryToTimeOfDay(hour, minute, out var timeOfDay))
            {
                return timeOfDay;
            }
        }

        var chinese = ChineseTimePattern.Match(text);
        if (!chinese.Success)
        {
            return null;
        }

        var chineseMinute = chinese.Groups[1].Success && chinese.Groups[2].Success
            ? chinese.Groups[2].Value.Trim() == "半" ? 30 : ParseInt(chinese.Groups[3].Value)
            : 0;

        return TryToTimeOfDay(ParseInt(chinese.Groups[1].Value), chineseMinute, out var parsed)
            ? parsed
            : null;
    }

    private static bool TryToTimeOfDay(int? hour, int? minute, out TimeSpan timeOfDay)
    {
        if (hour is null || minute is null || hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            timeOfDay = default;
            return false;
        }

        timeOfDay = new TimeSpan(hour.Value, minute.Value, 0);
        return true;
    }

    private static int? ParseInt(string text)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string Truncate(string text, int maxLength)
    {
        var collapsed = CollapseWhitespace(text);
        return collapsed.Length <= maxLength ? collapsed : collapsed[..(maxLength - 1)] + "…";
    }

    private static string CollapseWhitespace(string text)
        => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>关键词命中结果。</summary>
    private sealed record Classification(DigestCategory Category, string Label, IReadOnlyList<string> Hits);
}