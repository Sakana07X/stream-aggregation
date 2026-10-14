using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Core.Filtering;

/// <summary>
/// 闲聊过滤器：过滤纯表情、过短、无信息量的消息。
/// 包含高信号关键词（如 @所有人、紧急、告警等）的消息优先保留。
/// </summary>
public sealed class ChatterFilter : IMessageFilter
{
    private readonly FilteringOptions _options;

    /// <summary>
    /// 创建闲聊过滤器实例。
    /// </summary>
    /// <param name="options">过滤配置选项。</param>
    public ChatterFilter(FilteringOptions options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public IReadOnlyList<Message> Filter(IReadOnlyList<Message> messages)
    {
        var result = new List<Message>(messages.Count);

        foreach (var msg in messages)
        {
            if (IsHighSignal(msg.Text))
            {
                result.Add(msg);
                continue;
            }

            if (!IsChatter(msg.Text))
                result.Add(msg);
        }

        return result;
    }

    private bool IsHighSignal(string text)
    {
        foreach (var keyword in _options.HighSignalKeywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private bool IsChatter(string text)
    {
        var trimmed = text.Trim();

        // 空消息或低于最小长度
        if (trimmed.Length < _options.MinLength)
            return true;

        // 计算符号比例
        var symbolCount = trimmed.Count(IsSymbol);
        var ratio = (double)symbolCount / trimmed.Length;

        return ratio > _options.MaxSymbolRatio;
    }

    private static bool IsSymbol(char c)
    {
        // 非字母、非数字、非空白、非 CJK 统一表意文字的字符视为符号
        if (char.IsWhiteSpace(c))
            return false;

        // CJK 统一表意文字范围
        if (c >= 0x4E00 && c <= 0x9FFF)
            return false;

        // 字母和数字
        if (char.IsLetterOrDigit(c))
            return false;

        return true;
    }
}
