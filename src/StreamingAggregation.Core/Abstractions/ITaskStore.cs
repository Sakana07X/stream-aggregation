using System;
using System.Collections.Generic;
using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Core.Abstractions;

public interface ITaskStore
{
    /// <summary>
    /// 插入或更新行动项（UPSERT）——「后台刷新」语义。
    /// 更新业务字段（action/deadline/importance/reason/source_group_id/updated_at），
    /// <b>不修改用户状态列</b>（completed/ignored）。需要修改状态请使用
    /// <see cref="SetCompletedAsync"/> / <see cref="SetIgnoredAsync"/>。
    /// </summary>
    Task SaveAsync(ActionItem item, CancellationToken cancellationToken = default);

    /// <summary>
    /// 整批保存行动项：在<b>同一个事务</b>里写完全部条目，任一条失败则整批回滚。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么需要整批接口</b>：逐条 <see cref="SaveAsync"/> 允许"部分成功"，调用方只能靠逐条
    /// <c>try/catch</c> 兜底，于是摘要成功、行动项只写进去一半时，批次照样会被标成完成——
    /// 用户在摘要页看到待办，待办列表里却缺行，而且不会重来。这是静默丢数据。
    /// 放进一个事务之后，部分写入在<b>结构上</b>就不可能发生：要么全进，要么全不进。
    /// </para>
    /// <para>
    /// 语义与 <see cref="SaveAsync"/> 一致，仍是「后台刷新」的 UPSERT，不覆盖
    /// <c>completed</c> / <c>ignored</c> 两列用户状态。空列表是合法的空操作。
    /// </para>
    /// </remarks>
    /// <param name="items">本轮要保存的行动项；不传 <see langword="null"/>，空列表视作无操作。</param>
    /// <param name="cancellationToken">取消令牌；取消同样会导致整批不落库。</param>
    /// <exception cref="ArgumentException">列表中某个条目缺少 <c>Id</c> 或 <c>Action</c>。</exception>
    Task SaveAllAsync(IReadOnlyList<ActionItem> items, CancellationToken cancellationToken = default);

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
