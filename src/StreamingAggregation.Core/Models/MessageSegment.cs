using StreamingAggregation.Core.Diagnostics;

namespace StreamingAggregation.Core.Models;

/// <summary>
/// 消息片段：一条消息可拆分为多个片段（文本、图片、@提及等）。
/// </summary>
public sealed record MessageSegment
{
    /// <summary>片段类型标识（如 "text"、"image"、"at"）。</summary>
    public required string Type
    {
        get => field;
        init
        {
            Guard.NotNullOrWhiteSpace(value, nameof(Type));
            field = value;
        }
    }

    /// <summary>片段原始数据。</summary>
    public required string Data
    {
        get => field;
        init
        {
            Guard.NotNull(value, nameof(Data));
            field = value;
        }
    }
}
