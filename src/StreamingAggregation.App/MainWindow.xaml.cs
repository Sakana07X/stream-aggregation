using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StreamingAggregation.App.Composition;

namespace StreamingAggregation.App;

/// <summary>
/// 主窗口：NavigationView 导航壳 + 内容区 + 连接状态条。
/// </summary>
/// <remarks>
/// 页面实例由 <see cref="AppShell.PageFactory"/>（也就是组合根）构造并注入视图模型，
/// 因此这里用 <c>Frame.Content</c> 直接挂载而不是 <c>Frame.Navigate</c>：
/// 后者要求页面有可公开调用的无参构造，注入就无从谈起。代价是 <c>Frame</c> 自带的
/// 导航历史不可用，改用 <see cref="_history"/> 维护等价的"返回"能力。
/// </remarks>
public sealed partial class MainWindow : Window
{
    private readonly AppShell _shell;
    private readonly Stack<Page> _history = new();

    /// <summary>初始窗口宽度（物理像素）</summary>
    /// <remarks>
    /// WinUI 3 的 <see cref="Window"/> 没有 <c>Width</c>/<c>Height</c> 属性。把它们写进
    /// XAML 会被 XAML 编译器判为 WMC0011「Unknown member」而终止编译——而且 1.7 版
    /// 编译器在独立进程模式下会把错误吞掉，只留下一个退出码 1。因此初始尺寸只能运行时
    /// 经 <see cref="Window.AppWindow"/> 设置。
    /// </remarks>
    private const int InitialWindowWidth = 1100;

    /// <summary>初始窗口高度（物理像素）</summary>
    /// <inheritdoc cref="InitialWindowWidth" path="/remarks"/>
    private const int InitialWindowHeight = 720;

    /// <summary>
    /// 创建主窗口。
    /// </summary>
    /// <param name="shell">界面外壳上下文，提供视图模型与页面工厂。</param>
    /// <exception cref="ArgumentNullException"><paramref name="shell"/> 为 <see langword="null"/>。</exception>
    public MainWindow(AppShell shell)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));

        Title = "流式聚合";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        InitializeComponent();
        ApplyInitialWindowSize();

        StatusText.Text = BuildStatusText();
        _shell.ConnectionStatus.PropertyChanged += OnConnectionStatusChanged;
        Closed += OnClosed;
    }

    private void NavView_Loaded(object sender, RoutedEventArgs e)
    {
        if (_shell.FirstRun.IsFirstRun)
        {
            // 首次运行：显示欢迎遮罩层，不自动跳转，等用户选择「开始配置」或「稍后再说」。
            WelcomeOverlay.Visibility = Visibility.Visible;
        }
        else
        {
            WelcomeOverlay.Visibility = Visibility.Collapsed;
            Navigate(typeof(Pages.DigestPage), pushHistory: false);
            NavView.SelectedItem = NavView.MenuItems[0];
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        var pageType = item.Tag?.ToString() switch
        {
            "DigestPage" => typeof(Pages.DigestPage),
            "TodoPage" => typeof(Pages.TodoPage),
            "MessagesPage" => typeof(Pages.MessagesPage),
            "SettingsPage" => typeof(Pages.SettingsPage),
            _ => typeof(Pages.DigestPage),
        };

        Navigate(pageType, pushHistory: true);
    }

    private void NavView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (_history.Count == 0)
        {
            return;
        }

        ContentFrame.Content = _history.Pop();
        UpdateBackButton();
    }

    /// <summary>
    /// 「开始配置」：隐藏遮罩层并跳转到设置页。
    /// </summary>
    private void StartConfigButton_Click(object sender, RoutedEventArgs e)
    {
        WelcomeOverlay.Visibility = Visibility.Collapsed;
        NavView.SelectedItem = NavView.MenuItems[3]; // 设置页
    }

    /// <summary>
    /// 「稍后再说」：隐藏遮罩层、持久化标记（二次启动不再弹），并跳转到摘要页。
    /// </summary>
    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        WelcomeOverlay.Visibility = Visibility.Collapsed;
        _ = _shell.FirstRun.DismissAsync();
        Navigate(typeof(Pages.DigestPage), pushHistory: false);
        NavView.SelectedItem = NavView.MenuItems[0];
    }

    /// <summary>构造并显示目标页面。</summary>
    /// <param name="pageType">页面类型。</param>
    /// <param name="pushHistory">是否把当前页面压入返回栈。</param>
    private void Navigate(Type pageType, bool pushHistory)
    {
        if (pushHistory && ContentFrame.Content is Page current)
        {
            _history.Push(current);
        }

        ContentFrame.Content = _shell.PageFactory(pageType);
        UpdateBackButton();
    }

    /// <summary>按返回栈深度刷新"返回"按钮的可用状态。</summary>
    private void ApplyInitialWindowSize()
    {
        if (AppWindow is not { } appWindow)
        {
            return;
        }

        appWindow.Resize(new Windows.Graphics.SizeInt32(InitialWindowWidth, InitialWindowHeight));
    }

    private void UpdateBackButton() => NavView.IsBackEnabled = _history.Count > 0;

    /// <summary>拼接状态条文本：状态详情 + 已连接时长。</summary>
    /// <returns>状态条文本。</returns>
    private string BuildStatusText()
    {
        var duration = _shell.ConnectionStatus.DescribeConnectedDuration();

        return string.IsNullOrEmpty(duration)
            ? _shell.ConnectionStatus.DetailText
            : $"{_shell.ConnectionStatus.DetailText}（{duration}）";
    }

    /// <summary>把连接状态绑定上的文本同步到状态条。</summary>
    /// <param name="sender">连接状态绑定。</param>
    /// <param name="e">属性变更参数。</param>
    private void OnConnectionStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // DetailText 由 State/Endpoint/LastError 共同派生，任何一个变了都要刷新状态条。
        StatusText.Text = BuildStatusText();
    }

    /// <summary>窗口关闭时解绑，避免连接状态绑定继续持有已关闭窗口的处理器。</summary>
    /// <param name="sender">发送方。</param>
    /// <param name="e">事件参数。</param>
    private void OnClosed(object sender, WindowEventArgs args)
    {
        _shell.ConnectionStatus.PropertyChanged -= OnConnectionStatusChanged;
        Closed -= OnClosed;
    }
}