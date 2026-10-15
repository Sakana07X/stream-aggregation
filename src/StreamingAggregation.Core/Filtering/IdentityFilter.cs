using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Core.Filtering;

/// <summary>
/// 身份校验过滤器：丢弃缺少群 ID 或发送者 ID 的消息。
/// </summary>
/// <remarks>
/// <para>
/// OneBot 协议允许缺失 <c>group_id</c> / <c>user_id</c> 的群消息载荷到达客户端。
/// 解析层采取「归一化而非丢弃」策略，把这些字段留为空串；本过滤器负责在
/// <strong>业务管线入口</strong>把它们拦下，避免空身份的消息落库并进入摘要提示词。
/// </para>
/// <para>
/// 为什么不放在 WebSocket 客户端里：那里只应负责传输与协议映射，
/// 「什么算一条可用的群消息」属于业务规则，应由过滤层统一裁决。
/// </para>
/// </remarks>
public sealed class IdentityFilter : IMessageFilter
{
    /// <inheritdoc />
    public IReadOnlyList<Message> Filter(IReadOnlyList<Message> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var result = new List<Message>(messages.Count);

        foreach (var msg in messages)
        {
            if (HasIdentity(msg))
            {
                result.Add(msg);
            }
        }

        return result;
    }

    private static bool HasIdentity(Message msg)
        => !string.IsNullOrWhiteSpace(msg.GroupId)
           && !string.IsNullOrWhiteSpace(msg.Sender);
}