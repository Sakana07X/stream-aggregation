namespace StreamingAggregation.Core;

/// <summary>
/// 摘要分类。
/// </summary>
public enum DigestCategory
{
    /// <summary>紧急：需要立即处理。</summary>
    Urgent,

    /// <summary>待办：需要后续行动。</summary>
    Todo,

    /// <summary>学习：知识性内容，供后续查阅。</summary>
    Study,

    /// <summary>通知：公告或知会性内容。</summary>
    Notice,

    /// <summary>普通：无特殊归类。</summary>
    Normal
}

/// <summary>
/// 重要程度。
/// </summary>
public enum Importance
{
    High,
    Medium,
    Low
}

/// <summary>
/// 聚合批次状态。
/// </summary>
public enum BatchStatus
{
    /// <summary>已创建，等待处理。</summary>
    Pending,

    /// <summary>正在聚合处理中。</summary>
    Processing,

    /// <summary>聚合完成。</summary>
    Completed,

    /// <summary>聚合失败。</summary>
    Failed
}
