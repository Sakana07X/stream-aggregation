using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Core.Abstractions;

public interface ITaskStore
{
    /// <summary>插入或更新行动项（UPSERT）。</summary>
    Task SaveAsync(ActionItem item, CancellationToken cancellationToken = default);

    /// <summary>按 ID 获取行动项，不存在时返回 null。</summary>
    Task<ActionItem?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>获取待办列表：未完成且未忽略的行动项。</summary>
    Task<IReadOnlyList<ActionItem>> GetPendingAsync(CancellationToken cancellationToken = default);

    /// <summary>仅更新完成状态列，不影响其他字段。</summary>
    Task SetCompletedAsync(string id, bool completed, CancellationToken cancellationToken = default);

    /// <summary>仅更新忽略状态列，不影响其他字段。</summary>
    Task SetIgnoredAsync(string id, bool ignored, CancellationToken cancellationToken = default);

    /// <summary>按 ID 删除行动项。</summary>
    Task DeleteAsync(string id, CancellationToken cancellationToken = default);
}
