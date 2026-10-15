using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StreamingAggregation.App.Services;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Models;

namespace StreamingAggregation.App.Pages;

/// <summary>
/// 消息页：展示原始消息列表。
/// </summary>
/// <remarks>
/// <para>
/// <b>线程模型</b>：存储读取在线程池上继续，界面赋值一律经 <see cref="PageLoadContext.Apply"/>
/// 搬回 UI 线程；不要在别处直接给 <c>ItemsSource</c> 赋值。
/// </para>
/// </remarks>
public sealed partial class MessagesPage : Page
{
    /// <summary>页面最多展示的最近消息条数。</summary>
    private const int RecentMessageLimit = 200;

    private readonly IMessageStore _messageStore;
    private readonly PageLoadContext _load;

    /// <summary>
    /// 创建消息页。
    /// </summary>
    /// <param name="messageStore">消息存储，由组合根注入。</param>
    /// <param name="marshalToUi">
    /// 把界面更新搬到 UI 线程执行的封送委托；为 <see langword="null"/> 时就地执行。
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="messageStore"/> 为 <see langword="null"/>。</exception>
    public MessagesPage(IMessageStore messageStore, Action<Action>? marshalToUi = null)
    {
        _messageStore = messageStore ?? throw new ArgumentNullException(nameof(messageStore));
        _load = new PageLoadContext(nameof(MessagesPage), marshalToUi);

        InitializeComponent();
        Loaded += MessagesPage_Loaded;
    }

    private async void MessagesPage_Loaded(object sender, RoutedEventArgs e) =>
        await ReloadAsync().ConfigureAwait(true);

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await ReloadAsync().ConfigureAwait(true);

    /// <summary>
    /// 拉取最近消息并刷到界面上。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Message> messages;

        try
        {
            messages = await _messageStore
                .GetRecentAsync(RecentMessageLimit, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _load.Warn($"读取最近消息失败，已显示为空：{ex.Message}");
            messages = Array.Empty<Message>();
        }

        _load.Apply(() => MessagesListView.ItemsSource = messages);
    }
}