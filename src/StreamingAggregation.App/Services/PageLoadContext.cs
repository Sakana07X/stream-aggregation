using System;
using System.Diagnostics;

namespace StreamingAggregation.App.Services;

/// <summary>
/// 页面数据加载的公共支撑：把异步加载的结果搬回 UI 线程，并统一记录失败。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：存储层的任务（SQLite 往返）会在线程池上继续，若在 <c>await</c> 之后
/// 直接给 <c>ItemsSource</c> 赋值，更新就可能落在非 UI 线程上，WinUI 会当场抛
/// <see cref="System.Runtime.InteropServices.COMException"/>（RPC_E_WRONG_THREAD）。
/// 页面构造时把组合根的封送委托交进来，更新就一定回到 UI 线程；
/// 没给封送委托时（无 UI 场景）就地执行，与 <c>ConnectionStatusViewModel</c> 的约定一致。
/// </para>
/// <para>
/// <b>失败处理</b>：读不到数据不是致命错误，本类型只负责把异常记进 <see cref="Trace"/>，
/// 由调用方降级为空列表继续渲染——页面不该因为一条 SQL 失败就整个崩掉。
/// </para>
/// <para>
/// 刻意<b>不</b>在这里做取消与生命周期：每个页面都是短生命周期的视图对象，
/// 而 SQLite 查询是本地毫秒级往返，为此引入 <see cref="System.Threading.CancellationTokenSource"/>
/// 的所有权与释放时机会把复杂度抬到收益之上。真正需要终止的是整条消息管线，它由组合根负责。
/// </para>
/// </remarks>
internal sealed class PageLoadContext
{
    private readonly Action<Action> _marshal;

    /// <summary>
    /// 创建页面加载上下文。
    /// </summary>
    /// <param name="pageName">页面名，只用于日志前缀。</param>
    /// <param name="marshalToUi">
    /// 把一段界面更新搬到 UI 线程执行的封送委托；为 <see langword="null"/> 时就地执行。
    /// </param>
    public PageLoadContext(string pageName, Action<Action>? marshalToUi = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageName);

        PageName = pageName;
        _marshal = marshalToUi ?? (static action => action());
    }

    /// <summary>页面名（日志前缀）。</summary>
    public string PageName { get; }

    /// <summary>
    /// 在 UI 线程上执行一段界面更新。
    /// </summary>
    /// <remarks>
    /// 调用方<b>不要</b>直接给任何 WinUI 属性赋值，否则就绕开了封送通道——
    /// 这正是 <c>PageLoadContextTests</c> 要盯住的回归。
    /// </remarks>
    /// <param name="update">界面更新动作。</param>
    public void Apply(Action update)
    {
        ArgumentNullException.ThrowIfNull(update);

        _marshal(update);
    }

    /// <summary>记一条页面级告警。失败一律降级展示，不往界面上抛。</summary>
    /// <param name="message">告警内容。</param>
    public void Warn(string message)
    {
        Trace.WriteLine($"[{PageName}] {message}");
    }
}