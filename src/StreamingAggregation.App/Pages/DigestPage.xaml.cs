using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StreamingAggregation.App.Services;
using StreamingAggregation.Core;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Models;

namespace StreamingAggregation.App.Pages;

/// <summary>
/// 摘要页：按紧急/待办/学习/普通分组展示聚合摘要。
/// </summary>
/// <remarks>
/// <para>
/// <b>分类与版面一一对应</b>：XAML 里的四个分组标题（紧急/待办/学习/普通）分别对应
/// <see cref="DigestCategory"/> 的 <see cref="DigestCategory.Urgent"/>、<see cref="DigestCategory.Todo"/>、
/// <see cref="DigestCategory.Study"/>、<see cref="DigestCategory.Normal"/>。
/// 枚举里还有一个 <see cref="DigestCategory.Notice"/>（通知）没有对应分组，本版不展示——
/// 把通知塞进"普通"分组会让标题说谎，比暂不展示更糟。
/// </para>
/// <para>
/// <b>线程模型</b>：存储读取在线程池上继续，界面赋值一律经 <see cref="PageLoadContext.Apply"/>
/// 搬回 UI 线程；不要在别处直接给 <c>ItemsSource</c> 赋值。
/// </para>
/// </remarks>
public sealed partial class DigestPage : Page
{
    /// <summary>每个分组最多拉取的摘要条数。</summary>
    private const int CategoryLimit = 50;

    private readonly IDigestStore _digestStore;
    private readonly PageLoadContext _load;

    /// <summary>
    /// 创建摘要页。
    /// </summary>
    /// <param name="digestStore">摘要存储，由组合根注入。</param>
    /// <param name="marshalToUi">
    /// 把界面更新搬到 UI 线程执行的封送委托；为 <see langword="null"/> 时就地执行。
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="digestStore"/> 为 <see langword="null"/>。</exception>
    public DigestPage(IDigestStore digestStore, Action<Action>? marshalToUi = null)
    {
        _digestStore = digestStore ?? throw new ArgumentNullException(nameof(digestStore));
        _load = new PageLoadContext(nameof(DigestPage), marshalToUi);

        InitializeComponent();
        Loaded += DigestPage_Loaded;
    }

    private async void DigestPage_Loaded(object sender, RoutedEventArgs e) =>
        await ReloadAsync().ConfigureAwait(true);

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await ReloadAsync().ConfigureAwait(true);

    /// <summary>
    /// 按四个分类各取最近摘要，并一次性刷到界面上。
    /// </summary>
    /// <remarks>
    /// 四次查询并发发起：四个分类互不依赖，串行会把四段 SQLite 往返叠成总耗时。
    /// 任一分类读失败都单独降级为空列表——一个分类读不出来不应该让整页变空。
    /// </remarks>
    /// <param name="cancellationToken">取消令牌。</param>
    private async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<Digest>[] sections;

        try
        {
            sections = await Task.WhenAll(
                LoadCategoryAsync(DigestCategory.Urgent, cancellationToken),
                LoadCategoryAsync(DigestCategory.Todo, cancellationToken),
                LoadCategoryAsync(DigestCategory.Study, cancellationToken),
                LoadCategoryAsync(DigestCategory.Normal, cancellationToken))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 页面已被导航走，本次结果没人要了；静默返回，不当成错误。
            return;
        }

        _load.Apply(() =>
        {
            UrgentList.ItemsSource = sections[0];
            TodoList.ItemsSource = sections[1];
            StudyList.ItemsSource = sections[2];
            NormalList.ItemsSource = sections[3];
        });
    }

    /// <summary>
    /// 取单个分类下的最近摘要；失败降级为空列表并记日志。
    /// </summary>
    /// <param name="category">目标分类。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>摘要列表（按创建时间降序）；读取失败时为空列表。</returns>
    private async Task<IReadOnlyList<Digest>> LoadCategoryAsync(
        DigestCategory category,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _digestStore
                .GetByCategoryAsync(category, CategoryLimit, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _load.Warn($"读取“{category}”分类摘要失败，已显示为空：{ex.Message}");
            return Array.Empty<Digest>();
        }
    }
}