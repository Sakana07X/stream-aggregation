using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StreamingAggregation.Core.Models;

namespace StreamingAggregation.App.Pages;

/// <summary>
/// 消息页：展示原始消息列表。
/// </summary>
public sealed partial class MessagesPage : Page
{
    public MessagesPage()
    {
        InitializeComponent();
        Loaded += MessagesPage_Loaded;
    }

    private void MessagesPage_Loaded(object sender, RoutedEventArgs e)
    {
        // 首版接空集合占位
        MessagesListView.ItemsSource = Array.Empty<Message>();
    }
}
