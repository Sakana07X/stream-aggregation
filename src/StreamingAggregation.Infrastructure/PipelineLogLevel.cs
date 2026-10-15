namespace StreamingAggregation.Infrastructure;

/// <summary>
/// 管线日志级别。
/// </summary>
/// <remarks>
/// 原本定义在 App 的 <c>MessagePipeline</c> 里，但日志回调是编排层（<c>BatchProcessor</c>）
/// 与组合根之间的共同语言：编排要留在测试可加载的 Infrastructure，组合根要按同一个枚举落日志。
/// 因此枚举跟着编排一起下沉，两个程序集引用同一份定义，不做映射也不重复一份。
/// </remarks>
public enum PipelineLogLevel
{
    /// <summary>调试细节，日常可静音。</summary>
    Debug,

    /// <summary>正常生命周期事件。</summary>
    Info,

    /// <summary>可恢复的异常：某个批次或某条消息失败，其余流程继续。</summary>
    Warning,

    /// <summary>循环退出、发布失败等需要人工介入的情况。</summary>
    Error,
}