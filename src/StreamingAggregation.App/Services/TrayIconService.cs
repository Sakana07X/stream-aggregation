using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Diagnostics;

namespace StreamingAggregation.App.Services;

/// <summary>
/// 托盘常驻服务。使用 <c>H.NotifyIcon.Wpf</c> 创建系统托盘图标与右键菜单。
/// </summary>
/// <remarks>
/// <para>
/// <b>WinUI 3 → WPF 的对应关系</b>（照抄 WinUI 写法会直接编译失败）：
/// <list type="bullet">
/// <item><description>包换成 <c>H.NotifyIcon.Wpf</c>，但<b>命名空间仍是 <c>H.NotifyIcon</c></b>
/// ——包名带 <c>.Wpf</c>，类型名不带。</description></item>
/// <item><description><c>ContextFlyout</c>（<c>MenuFlyout</c> / <c>MenuFlyoutItem</c>）
/// → WPF 原生 <see cref="ContextMenu"/> / <see cref="MenuItem"/>。</description></item>
/// <item><description>菜单展开的触发方式由 <c>ContextMenuMode</c> 改为
/// <see cref="TaskbarIcon.MenuActivation"/>（<c>PopupActivationMode</c>）。
/// 本应用固定 <see cref="PopupActivationMode.RightClick"/>：左键留给"打开窗口"。</description></item>
/// <item><description><c>Microsoft.UI.Xaml.Visibility</c> → <see cref="Visibility"/>；
/// <c>Microsoft.UI.Xaml.Automation.AutomationProperties</c> →
/// <see cref="System.Windows.Automation.AutomationProperties"/>（需要引用 UIAutomationTypes）。</description></item>
/// <item><description>WinUI 版靠 XAML 树加载自动建图标；本服务在纯代码里构造，
/// 因此必须显式 <see cref="TaskbarIcon.ForceCreate"/>，否则图标不会进任务栏。</description></item>
/// </list>
/// </para>
/// <para>
/// <b>线程约束</b>：H.NotifyIcon 要求创建方在 STA 线程且线程上有 <c>Dispatcher</c>。
/// 本服务由组合根在 UI 线程上创建（WPF 生成的 <c>Main</c> 带 <c>[STAThread]</c>），
/// 该约束自动满足。
/// </para>
/// </remarks>
public sealed class TrayIconService : IDisposable
{
    private readonly TaskbarIcon _trayIcon;
    private readonly Func<Window> _windowProvider;
    private readonly Func<bool> _isPausedProvider;
    private readonly Action _togglePauseAction;
    private readonly Action _exitAction;
    private readonly MenuItem _pauseItem;
    private bool _disposed;

    /// <summary>
    /// 创建托盘图标与右键菜单。
    /// </summary>
    /// <param name="windowProvider">取主窗口的委托；可能返回 <see langword="null"/>（窗口已释放）。</param>
    /// <param name="isPausedProvider">当前是否处于暂停状态的判定。</param>
    /// <param name="togglePauseAction">切换暂停/恢复的动作。</param>
    /// <param name="exitAction">退出应用的动作。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public TrayIconService(
        Func<Window> windowProvider,
        Func<bool> isPausedProvider,
        Action togglePauseAction,
        Action exitAction)
    {
        _windowProvider = Guard.NotNull(windowProvider);
        _isPausedProvider = Guard.NotNull(isPausedProvider);
        _togglePauseAction = Guard.NotNull(togglePauseAction);
        _exitAction = Guard.NotNull(exitAction);

        _pauseItem = new MenuItem();

        _trayIcon = new TaskbarIcon
        {
            Icon = ExtractAssociatedIcon(),
            ToolTipText = "流式聚合",
            MenuActivation = PopupActivationMode.RightClick,
            LeftClickCommand = new RelayCommand(ShowWindow),
            NoLeftClickDelay = true,
        };

        _trayIcon.ContextMenu = BuildContextMenu();
        _trayIcon.Visibility = Visibility.Visible;

        // 纯代码构造，没有任何 XAML 树会去加载这个控件，必须显式建图标。
        // 传 true 一并开启 Windows 11 的「效率模式」，常驻应用本就该让出调度优先级。
        _trayIcon.ForceCreate(true);
    }

    /// <summary>
    /// 按当前连接状态刷新托盘提示文字。
    /// </summary>
    /// <param name="state">连接状态。</param>
    public void UpdateConnectionState(ConnectionState state)
    {
        if (_disposed)
        {
            return;
        }

        var text = state switch
        {
            ConnectionState.Connected => "流式聚合 — 已连接",
            ConnectionState.Connecting => "流式聚合 — 正在连接…",
            ConnectionState.Reconnecting => "流式聚合 — 正在重连…",
            _ => "流式聚合 — 未连接",
        };

        _trayIcon.ToolTipText = _isPausedProvider()
            ? $"{text}（已暂停）"
            : text;
    }

    /// <summary>
    /// 按当前暂停状态刷新「暂停处理 / 恢复处理」菜单文案与无障碍名称。
    /// </summary>
    public void UpdatePauseState()
    {
        if (_disposed)
        {
            return;
        }

        var text = _isPausedProvider() ? "恢复处理" : "暂停处理";
        _pauseItem.Header = text;
        AutomationProperties.SetName(_pauseItem, text);
    }

    /// <summary>释放托盘图标；重复调用安全。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _trayIcon.Dispose();
    }

    /// <summary>显示主窗口并把它拉到前台。</summary>
    private void ShowWindow()
    {
        var window = _windowProvider();
        if (window is null)
        {
            return;
        }

        // 托盘常驻时窗口处于 Hide 状态，必须先 Show 才能 Activate。
        if (!window.IsVisible)
        {
            window.Show();
        }

        // 被最小化时先还原，否则 Activate() 只能把一个最小化的窗口顶到前台，看不出变化。
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    /// <summary>
    /// 构造托盘右键菜单。
    /// </summary>
    /// <remarks>
    /// 暂停项的文案由 <see cref="UpdatePauseState"/> 按状态刷新，因此这里只负责把
    /// <c>Command</c> 绑到 <c>_togglePauseAction</c> 上；<c>Tag</c> 保留供自动化定位。
    /// </remarks>
    /// <returns>菜单。</returns>
    private ContextMenu BuildContextMenu()
    {
        var openItem = new MenuItem { Header = "打开窗口" };
        openItem.Command = new RelayCommand(ShowWindow);

        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Command = new RelayCommand(() => _exitAction());

        _pauseItem.Command = new RelayCommand(_togglePauseAction);
        _pauseItem.Tag = "PauseMenuItem";

        var menu = new ContextMenu();
        menu.Items.Add(openItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        // 构造期先把文案定下来：菜单项在创建的那一帧就可能被用户看到。
        UpdatePauseState();

        return menu;
    }

    /// <summary>
    /// 取 exe 内嵌图标作为托盘图标。
    /// </summary>
    /// <remarks>
    /// 图标来自 csproj 的 <c>ApplicationIcon</c>（<c>Assets\AppIcon.ico</c>），由编译器写进 PE 头，
    /// 因此发布目录里没有单独的 .ico 也不会丢图标。取不到时退回系统应用图标：
    /// 托盘没有图标等于应用看起来没启动，这比图标难看严重得多。
    /// </remarks>
    /// <returns>托盘图标。</returns>
    private static Icon ExtractAssociatedIcon()
    {
        try
        {
            var path = Assembly.GetExecutingAssembly().Location;
            var icon = string.IsNullOrEmpty(path) ? null : Icon.ExtractAssociatedIcon(path);
            if (icon is not null)
            {
                return icon;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[TrayIcon] 读取内嵌图标失败，改用系统图标：{ex.GetType().Name}: {ex.Message}");
        }

        return SystemIcons.Application;
    }

    /// <summary>
    /// 最小 <see cref="ICommand"/> 实现：命令永远可执行，不参与 <c>CanExecuteChanged</c>。
    /// </summary>
    /// <remarks>
    /// 托盘菜单三项都是即时动作，没有可用性状态可言；引入 MVVM 框架的命令基础设施
    /// 只为换掉这 20 行代码，不划算。
    /// </remarks>
    private sealed class RelayCommand : ICommand
    {
        private readonly Action _action;

        /// <summary>创建命令。</summary>
        /// <param name="action">被执行的委托。</param>
        public RelayCommand(Action action) => _action = Guard.NotNull(action);

        /// <summary>恒为 <see langword="true"/>。</summary>
        /// <param name="parameter">忽略。</param>
        /// <returns>恒为 <see langword="true"/>。</returns>
        public bool CanExecute(object? parameter) => true;

        /// <summary>执行被封装的委托。</summary>
        /// <param name="parameter">忽略。</param>
        public void Execute(object? parameter) => _action();

        /// <summary>空事件：可执行性恒定，订阅方不需要任何通知。</summary>
#pragma warning disable CS0067 // 事件从未触发，属有意设计。
        public event EventHandler? CanExecuteChanged
#pragma warning restore CS0067
        {
            add { }
            remove { }
        }
    }
}