namespace StreamingAggregation.Core.Filtering;

/// <summary>
/// 消息过滤配置选项。
/// </summary>
public sealed class FilteringOptions
{
    /// <summary>消息最小长度（字符数），低于此值视为闲聊。默认 2。</summary>
    public int MinLength { get; set; } = 2;

    /// <summary>
    /// 符号（表情、标点等非文字字符）占消息总字符的最大比例，
    /// 超过此值视为无信息量。默认 0.8。
    /// </summary>
    public double MaxSymbolRatio { get; set; } = 0.8;

    /// <summary>
    /// 高信号关键词列表，包含这些关键词的消息优先保留，不受闲聊过滤影响。
    /// </summary>
    public IReadOnlyList<string> HighSignalKeywords { get; set; } = new[]
    {
        "@所有人",
        "@all",
        "紧急",
        "urgent",
        "告警",
        "报警",
        "故障",
        "宕机",
        "down",
        "事故",
        "P0",
        "P1",
    };
}
