using System;
using System.ComponentModel;
using System.Windows;
using StreamingAggregation.App.Composition;
using StreamingAggregation.Core.Diagnostics;

namespace StreamingAggregation.App;

/// <summary>
/// 主窗口。阶段 2 只负责「一个能被打开、能被关掉、能托盘常驻的空壳」。
/// </summary>
/// <remarks>
/// <para>
/// 窗口标题与初始尺寸不再在 code-behind 里设置：<see cref="Window.Title"/> 与
/// <c>Width</c>/<c>Height</c> 都是普通依赖属性，直接写在 <c>MainWindow.xaml</c> 里，
/// 设计与运行时的取值只有一处来源。
/// </para>
/// <para>
/// <b>关窗 ≠ 退出</b>：托盘常驻模式下 <see cref="OnClosing"/> 会吃掉关闭请求并改为隐藏窗口，
/// 真正的退出只由托盘菜单触发。这不是"拦截用户操作"，而是这个应用的产品形态——
/// 它本来就是一个常驻后台的消息聚合器，窗口只是它的设置界面。
/// 想改成"关窗即退出"请用命令行 <c>--exit-on-window-close</c>，见 <c>ShutdownModeResolver</c>。
/// </para>
/// <para>
/// TODO(阶段 3): 导航壳与内容区接线（<c>Shell.PageFactory</c>、连接状态条、返回栈）。
/// TODO(阶段 4): 首启欢迎遮罩层（<c>Shell.FirstRun.IsFirstRun</c> 已在 <see cref="Shell"/> 就绪）。
/// </para>
/// </remarks>
public sealed partial class MainWindow : Window
{
    private readonly AppShell _shell;

    /// <summary>是否已经进入真正的关闭流程（托盘「退出」或应用退出时置位）。</summary>
    private bool _allowClose;

    /// <summary>
    /// 创建主窗口。
    /// </summary>
    /// <param name="shell">界面外壳上下文，提供视图模型与页面工厂。</param>
    /// <exception cref="ArgumentNullException"><paramref name="shell"/> 为 <see langword="null"/>。</exception>
    public MainWindow(AppShell shell)
    {
        _shell = Guard.NotNull(shell);

        Title = "流式聚合";
        InitializeComponent();

        Closing += OnClosing;
    }

    /// <summary>界面外壳上下文：视图模型与页面工厂的来源。阶段 3 的导航逻辑从这里取。</summary>
    public AppShell Shell => _shell;

    /// <summary>
    /// 请求真正关闭本窗口，绕过 <see cref="OnClosing"/> 里的"关窗即隐藏"拦截。
    /// </summary>
    /// <remarks>
    /// 只给"应用即将退出"的路径用（组合根的 <c>Dispose</c> / 托盘「退出」）。
    /// 对已关闭的窗口调用是安全的空操作。
    /// </remarks>
    public void RequestClose()
    {
        _allowClose = true;
        Close();
    }

    /// <summary>
    /// 拦截关闭请求：托盘常驻模式下只隐藏窗口，进程与托盘继续存活。
    /// </summary>
    /// <param name="sender">发送方。</param>
    /// <param name="e">关闭参数；置 <see cref="CancelEventArgs.Cancel"/> 即放弃本次关闭。</param>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // 两种情况放行：① 应用已经决定要退出（托盘「退出」/ Dispose）；
        // ② 关闭模式是"关窗即退出"（OnLastWindowClose），此时 WPF 自己会负责收尾进程。
        if (_allowClose || Application.Current?.ShutdownMode == ShutdownMode.OnLastWindowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }
}