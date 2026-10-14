using Microsoft.UI.Xaml.Controls;
using StreamingAggregation.Core;
using StreamingAggregation.Core.Models;

namespace StreamingAggregation.App.Pages;

/// <summary>
/// 摘要页：按紧急/待办/学习/普通分组展示聚合摘要。
/// </summary>
public sealed partial class DigestPage : Page
{
    public DigestPage()
    {
        InitializeComponent();
        Loaded += DigestPage_Loaded;
    }

    private void DigestPage_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // 首版接空集合占位
        UrgentList.ItemsSource = Array.Empty<Digest>();
        TodoList.ItemsSource = Array.Empty<Digest>();
        StudyList.ItemsSource = Array.Empty<Digest>();
        NormalList.ItemsSource = Array.Empty<Digest>();
    }
}
