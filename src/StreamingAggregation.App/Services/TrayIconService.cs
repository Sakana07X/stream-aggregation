using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StreamingAggregation.Core.Abstractions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace StreamingAggregation.App.Services;

/// <summary>
/// 托盘常驻服务。使用 H.NotifyIcon.WinUI 创建系统托盘图标与右键菜单。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly TaskbarIcon _trayIcon;
    private readonly Func<Window> _windowProvider;
    private readonly Func<bool> _isPausedProvider;
    private readonly Action _exitAction;
    private bool _disposed;

    public TrayIconService(
        Func<Window> windowProvider,
        Func<bool> isPausedProvider,
        Action exitAction)
    {
        _windowProvider = windowProvider ?? throw new ArgumentNullException(nameof(windowProvider));
        _isPausedProvider = isPausedProvider ?? throw new ArgumentNullException(nameof(isPausedProvider));
        _exitAction = exitAction ?? throw new ArgumentNullException(nameof(exitAction));

        _trayIcon = new TaskbarIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(
                System.Reflection.Assembly.GetExecutingAssembly().Location),
            ToolTipText = "流式聚合",
            ContextMenuMode = ContextMenuMode.SecondWindow,
            LeftClickCommand = new RelayCommand(ShowWindow),
            NoLeftClickDelay = true,
        };

        _trayIcon.ContextFlyout = BuildContextMenu();
        _trayIcon.Visibility = Visibility.Visible;
    }

    public void UpdateConnectionState(ConnectionState state)
    {
        if (_disposed) return;

        string text = state switch
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

    private void ShowWindow()
    {
        var window = _windowProvider();
        if (window is null) return;

        window.Activate();
    }

    private MenuFlyout BuildContextMenu()
    {
        var menu = new MenuFlyout();

        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "打开窗口",
            Command = new RelayCommand(ShowWindow),
        });

        var pauseItem = new MenuFlyoutItem
        {
            Text = "暂停处理",
            Tag = "PauseMenuItem",
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pauseItem, "暂停处理");
        menu.Items.Add(pauseItem);

        menu.Items.Add(new MenuFlyoutItem
        {
            Text = "退出",
            Command = new RelayCommand(() => _exitAction()),
        });

        return menu;
    }

    private class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _action;

        public RelayCommand(Action action)
        {
            _action = action ?? throw new ArgumentNullException(nameof(action));
        }

        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => _action();
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _trayIcon?.Dispose();
    }
}
