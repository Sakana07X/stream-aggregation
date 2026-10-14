using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Core.Filtering;

/// <summary>
/// 消息过滤器接口，用于在聚合前对消息进行筛选。
/// </summary>
public interface IMessageFilter
{
    /// <summary>
    /// 对输入消息列表执行过滤，返回过滤后的消息列表。
    /// </summary>
    /// <param name="messages">待过滤的消息列表。</param>
    /// <returns>过滤后的消息列表。</returns>
    IReadOnlyList<Message> Filter(IReadOnlyList<Message> messages);
}
