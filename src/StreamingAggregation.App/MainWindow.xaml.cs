using System;
using System.ComponentModel;
using System.Windows;
using StreamingAggregation.App.Composition;
using StreamingAggregation.App.Navigation;
using StreamingAggregation.Core.Diagnostics;
using StreamingAggregation.Infrastructure.ViewModels;

namespace StreamingAggregation.App;

/// <summary>
/// 主窗口：导航壳 + 连接状态条 + 首启遮罩层宿主。
/// </summary>
/// <remarks>
/// <para>
/// <b>关窗 ≠ 退出</b>：托盘常驻模式下 <see cref="OnClosing"/> 会吃掉关闭请求并改为隐藏窗口，
/// 真正的退出只由托盘菜单触发。想改成"关窗即退出"请用命令行 <c>--exit-on-window-close</c>。
/// </para>
/// <para>
/// <b>首启遮罩层</b>：<c>NavShell</c> 内的 <c>WelcomeOverlay</c> 由 <see cref="OnNavShellLoaded"/>
/// 根据 <c>Shell.FirstRun.IsFirstRun</c> 控制可见性。
/// </para>
/// </remarks>
public sealed partial class MainWindow : Window
{
    private readonly AppShell _shell;
    private bool _allowClose;
    private bool _isNavShellLoaded;

    public MainWindow(AppShell shell)
    {
        _shell = Guard.NotNull(shell);
        Title = "流式聚合";
        InitializeComponent();
        Closing += OnClosing;
        MainNavShell.DataContext = shell;
        MainNavShell.Initialize(shell.PageFactory);
    }

    public AppShell Shell => _shell;

    /// <summary>请求真正关闭本窗口，绕过 <see cref="OnClosing"/> 的"关窗即隐藏"拦截。</summary>
    public void RequestClose()
    {
        _allowClose = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || Application.Current?.ShutdownMode == ShutdownMode.OnLastWindowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    /// <summary>
    /// NavShell 加载完成后接线首启遮罩层与状态条。
    /// </summary>
    private void OnNavShellLoaded(object sender, RoutedEventArgs e)
    {
        if (_isNavShellLoaded) return;
        _isNavShellLoaded = true;

        // 首启遮罩层
        MainNavShell.SetWelcomeOverlayVisible(_shell.FirstRun.IsFirstRun);

        // 状态条绑定
        _shell.ConnectionStatus.PropertyChanged += OnConnectionStatusChanged;
        UpdateStatusText(_shell.ConnectionStatus.DetailText);
    }

    private void OnConnectionStatusChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionStatusViewModel.DetailText))
        {
            UpdateStatusText(_shell.ConnectionStatus.DetailText);
        }
    }

    private void UpdateStatusText(string? text)
    {
        StatusTextBlock.Text = text ?? string.Empty;
    }
}
