using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StreamingAggregation.Core.Models;

namespace StreamingAggregation.App.Pages;

/// <summary>
/// 待办页：展示行动项，支持完成/忽略操作。
/// </summary>
public sealed partial class TodoPage : Page
{
    public TodoPage()
    {
        InitializeComponent();
        Loaded += TodoPage_Loaded;
    }

    private void TodoPage_Loaded(object sender, RoutedEventArgs e)
    {
        // 首版接空集合占位
        TodoListView.ItemsSource = Array.Empty<ActionItem>();
    }

    private void CompleteButton_Click(object sender, RoutedEventArgs e)
    {
        // TODO(T15+): 接入实际完成逻辑
    }

    private void IgnoreButton_Click(object sender, RoutedEventArgs e)
    {
        // TODO(T15+): 接入实际忽略逻辑
    }
}
