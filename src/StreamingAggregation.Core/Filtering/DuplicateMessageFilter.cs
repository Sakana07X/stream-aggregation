using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Core.Filtering;

/// <summary>
/// 同群、同内容、同时间窗口内的消息去重过滤器。
/// 保留每组重复消息中的第一条，后续重复项被移除。
/// </summary>
public sealed class DuplicateMessageFilter : IMessageFilter
{
    private readonly TimeSpan _window;

    /// <summary>
    /// 创建去重过滤器实例。
    /// </summary>
    /// <param name="window">去重时间窗口，同一群组在此窗口内相同内容的消息被视为重复。</param>
    public DuplicateMessageFilter(TimeSpan window)
    {
        _window = window;
    }

    /// <inheritdoc />
    public IReadOnlyList<Message> Filter(IReadOnlyList<Message> messages)
    {
        if (messages.Count == 0)
            return [];

        // 按时间排序，确保保留的是最早的那条
        var sorted = messages.OrderBy(m => m.Timestamp).ToList();
        var result = new List<Message>(sorted.Count);
        // Key: (GroupId, NormalizedText) -> 该组最新一条消息的时间戳
        var lastSeen = new Dictionary<(string, string), DateTime>(sorted.Count);

        foreach (var msg in sorted)
        {
            var key = (msg.GroupId, Normalize(msg.Text));

            if (lastSeen.TryGetValue(key, out var lastTime))
            {
                // 在窗口内，视为重复，跳过
                if (msg.Timestamp - lastTime <= _window)
                    continue;
            }

            lastSeen[key] = msg.Timestamp;
            result.Add(msg);
        }

        return result;
    }

    private static string Normalize(string text)
    {
        // 去除首尾空白并压缩连续空白，避免格式差异导致漏判
        return string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
