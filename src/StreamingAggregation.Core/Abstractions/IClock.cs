namespace StreamingAggregation.Core.Abstractions;

/// <summary>
/// 时钟接口，用于获取当前时间。
/// 在测试中使用 FakeClock 替代真实时间源。
/// </summary>
public interface IClock
{
    /// <summary>
    /// 获取当前时间。
    /// </summary>
    /// <remarks>
    /// <b>契约：返回值必须是 <see cref="DateTimeKind.Utc"/>。</b>
    /// <para>
    /// 下游会直接拿 <see cref="Now"/> 与 UTC 窗口边界做 <c>Ticks</c> 数值比较
    /// （<c>TimeWindowAggregator</c> 的 <c>FlushAsync</c> / <c>RestoreAsync</c>）。
    /// 若实现返回 <see cref="DateTimeKind.Local"/> 或
    /// <see cref="DateTimeKind.Unspecified"/>，同样的墙钟时刻会被当作不同时刻，
    /// 导致窗口提前或推迟开启。
    /// </para>
    /// <para>
    /// 需要其他时区表示时由调用方负责换算，时钟本身不暴露本地时间。
    /// </para>
    /// </remarks>
    DateTime Now { get; }
}
