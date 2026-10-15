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
/// 待办页：展示行动项，支持完成/忽略操作。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么操作后整表重拉</b>：待办列表本身就是"未完成且未忽略"的查询结果。
/// 改完状态列后本地删一行看着更轻，但后台摘要可能正在往同一张表写行动项，
/// 本地拼列表迟早会和库里对不上；重拉一次成本是一次本地查询，换来的是"界面即真相"。
/// </para>
/// <para>
/// <b>线程模型</b>：存储读取与写入在线程池上继续，界面赋值一律经 <see cref="PageLoadContext.Apply"/>
/// 搬回 UI 线程；不要在别处直接给 <c>ItemsSource</c> 赋值。
/// </para>
/// </remarks>
public sealed partial class TodoPage : Page
{
    private readonly ITaskStore _taskStore;
    private readonly PageLoadContext _load;

    /// <summary>是否有一次状态写入正在进行（0=空闲，1=进行中）。点击回调在 UI 线程，读写用原子操作。</summary>
    private int _isMutating;

    /// <summary>
    /// 创建待办页。
    /// </summary>
    /// <param name="taskStore">行动项存储，由组合根注入。</param>
    /// <param name="marshalToUi">
    /// 把界面更新搬到 UI 线程执行的封送委托；为 <see langword="null"/> 时就地执行。
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="taskStore"/> 为 <see langword="null"/>。</exception>
    public TodoPage(ITaskStore taskStore, Action<Action>? marshalToUi = null)
    {
        _taskStore = taskStore ?? throw new ArgumentNullException(nameof(taskStore));
        _load = new PageLoadContext(nameof(TodoPage), marshalToUi);

        InitializeComponent();
        Loaded += TodoPage_Loaded;
    }

    private async void TodoPage_Loaded(object sender, RoutedEventArgs e) =>
        await ReloadAsync().ConfigureAwait(true);

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await ReloadAsync().ConfigureAwait(true);

    /// <summary>
    /// 拉取待办列表并刷到界面上。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ActionItem> pending;

        try
        {
            pending = await _taskStore.GetPendingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _load.Warn($"读取待办列表失败，已显示为空：{ex.Message}");
            pending = Array.Empty<ActionItem>();
        }

        _load.Apply(() => TodoListView.ItemsSource = pending);
    }

    private async void CompleteButton_Click(object sender, RoutedEventArgs e) =>
        await MutateAsync(sender, ignored: false).ConfigureAwait(true);

    private async void IgnoreButton_Click(object sender, RoutedEventArgs e) =>
        await MutateAsync(sender, ignored: true).ConfigureAwait(true);

    /// <summary>
    /// 把某一行的完成/忽略状态写回存储，然后重拉列表。
    /// </summary>
    /// <remarks>
    /// 写失败不弹窗也不中断，只记日志后照常重拉——用户看到的仍然是数据库里的真实状态，
    /// 总比弹一个"操作失败"却留着错误状态行要好。
    /// </remarks>
    /// <param name="sender">触发点击的按钮，其 <see cref="FrameworkElement.Tag"/> 携带行动项 ID。</param>
    /// <param name="ignored"><see langword="true"/> 表示忽略，<see langword="false"/> 表示完成。</param>
    private async Task MutateAsync(object sender, bool ignored)
    {
        if (sender is not FrameworkElement { Tag: string id } || string.IsNullOrWhiteSpace(id))
        {
            _load.Warn("完成/忽略操作缺少行动项标识，本次点击已忽略。");
            return;
        }

        // 连点保护：上一次写入还在飞就不要发第二次，否则两次重拉会打架。
        if (Interlocked.CompareExchange(ref _isMutating, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (ignored)
            {
                await _taskStore.SetIgnoredAsync(id, true).ConfigureAwait(false);
            }
            else
            {
                await _taskStore.SetCompletedAsync(id, true).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _load.Warn($"更新待办状态失败（{id}）：{ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _isMutating, 0);
        }

        await ReloadAsync().ConfigureAwait(false);
    }
}