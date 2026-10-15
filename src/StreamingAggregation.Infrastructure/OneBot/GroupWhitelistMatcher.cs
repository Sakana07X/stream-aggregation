using System.Collections.Frozen;
using StreamingAggregation.Core.Configuration;

namespace StreamingAggregation.Infrastructure.OneBot;

/// <summary>
/// 按 group_id 过滤群消息白名单。
/// </summary>
/// <remarks>
/// 白名单为空时一律不匹配（fail closed）：设置未配置或读取失败时，宁可什么都不聚合，
/// 也不要意外把无关群聊送进摘要。界面应通过 <see cref="IsEmpty"/> 提示用户去配置。
/// </remarks>
public sealed class GroupWhitelistMatcher
{
    /// <summary>空白名单实例，恒不匹配。</summary>
    public static readonly GroupWhitelistMatcher Empty = new(Array.Empty<string>());

    private readonly FrozenSet<string> _groupIds;

    /// <summary>
    /// 用群 ID 列表构造。空白项会被剔除，前后空白会被裁剪，重复项自动合并。
    /// </summary>
    public GroupWhitelistMatcher(IEnumerable<string>? groupIds)
    {
        _groupIds = (groupIds ?? Array.Empty<string>())
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id.Trim())
            .ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>生效的群 ID 集合。</summary>
    public IReadOnlyCollection<string> GroupIds => _groupIds;

    /// <summary>白名单是否为空。为 true 时所有群消息都会被拒绝。</summary>
    public bool IsEmpty => _groupIds.Count == 0;

    /// <summary>从配置对象构造，配置缺失时退化为空白名单。</summary>
    public static GroupWhitelistMatcher FromSettings(GroupWhitelist? settings)
        => settings is null ? Empty : new GroupWhitelistMatcher(settings.GroupIds);

    /// <summary>判断群 ID 是否在白名单内。</summary>
    public bool Matches(string? groupId)
        => !string.IsNullOrWhiteSpace(groupId) && _groupIds.Contains(groupId.Trim());
}