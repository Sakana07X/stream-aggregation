using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using StreamingAggregation.App.Composition;
using StreamingAggregation.Core.Diagnostics;

namespace StreamingAggregation.App.Navigation;

/// <summary>
/// WPF 导航壳：替代 WinUI 的 NavigationView。
/// 左侧 ListBox 导航 + 右侧 ContentControl 内容区 + 首启遮罩层。
/// </summary>
public sealed partial class NavShell : UserControl
{
    private readonly Stack<Page> _history = new();
    private Func<Type, Page>? _pageFactory;

    /// <summary>
    /// 当前内容页是否可后退（历史栈非空）。
    /// </summary>
    public bool CanGoBack => _history.Count > 0;

    /// <summary>
    /// 默认构造函数（供 XAML 设计器使用；运行时由 <see cref="Initialize"/> 注入工厂）。
    /// </summary>
    public NavShell() { }

    /// <summary>
    /// 注入页面工厂并完成初始化（由 AppCompositionRoot 在运行时调用）。
    /// </summary>
    public void Initialize(Func<Type, Page> pageFactory)
    {
        _pageFactory = pageFactory ?? throw new ArgumentNullException(nameof(pageFactory));
        InitializeComponent();

        // 阶段 4 页面迁移前，CreatePage 会抛 NotSupportedException。
        // 此时导航壳仍可用（遮罩层、状态条、导航项），只是内容区为空。
        try
        {
            NavigateTo("DigestPage");
        }
        catch (NotSupportedException)
        {
            // 页面尚未迁移，内容区留空；阶段 4 迁移后自动恢复。
        }
    }

    /// <summary>
    /// 按 Tag 解析页面类型（阶段 4 页面迁移后注入真实类型）。
    /// </summary>
    private static Type ResolvePageType(string pageTag) => pageTag switch
    {
        "DigestPage" => Type.GetType("StreamingAggregation.App.Pages.DigestPage, StreamingAggregation.App") ?? throw new InvalidOperationException("DigestPage 未加载"),
        "TodoPage" => Type.GetType("StreamingAggregation.App.Pages.TodoPage, StreamingAggregation.App") ?? throw new InvalidOperationException("TodoPage 未加载"),
        "MessagesPage" => Type.GetType("StreamingAggregation.App.Pages.MessagesPage, StreamingAggregation.App") ?? throw new InvalidOperationException("MessagesPage 未加载"),
        "SettingsPage" => Type.GetType("StreamingAggregation.App.Pages.SettingsPage, StreamingAggregation.App") ?? throw new InvalidOperationException("SettingsPage 未加载"),
        _ => throw new ArgumentException($"未知页面: {pageTag}", nameof(pageTag))
    };

    /// <summary>
    /// 按导航项 Tag（页面类型名）切换页面。
    /// </summary>
    public void NavigateTo(string pageTag)
    {
        Guard.NotNull(_pageFactory, nameof(_pageFactory));
        var page = _pageFactory!(ResolvePageType(pageTag));
        if (ContentFrame.Content is not null)
        {
            _history.Push((Page)ContentFrame.Content);
        }
        ContentFrame.Content = page;
        UpdateNavSelection(pageTag);
    }

    /// <summary>
    /// 后退到上一个页面。
    /// </summary>
    public void GoBack()
    {
        if (_history.Count == 0) return;
        ContentFrame.Content = _history.Pop();
        UpdateNavSelection(((Page)ContentFrame.Content).GetType().Name);
    }

    /// <summary>
    /// 显示/隐藏首启遮罩层。
    /// </summary>
    public void SetWelcomeOverlayVisible(bool visible)
    {
        WelcomeOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateNavSelection(string pageTag)
    {
        foreach (ListBoxItem item in NavList.Items)
        {
            if (item.Tag?.ToString() == pageTag)
            {
                item.IsSelected = true;
                break;
            }
        }
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count == 0) return;
        if (e.AddedItems[0] is ListBoxItem item && item.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => GoBack();

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        // 跳到设置页并关闭遮罩
        NavigateTo("SettingsPage");
        if (DataContext is AppShell shell)
        {
            _ = shell.FirstRun.DismissAsync();
        }
        SetWelcomeOverlayVisible(false);
    }

    private void LaterButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is AppShell shell)
        {
            _ = shell.FirstRun.DismissAsync();
        }
        SetWelcomeOverlayVisible(false);
    }
}
