using System;
using Microsoft.UI.Xaml.Controls;
using StreamingAggregation.App.ViewModels;

namespace StreamingAggregation.App.Pages;

/// <summary>
/// 设置页：SnowLuma 连接、群白名单、模型配置、聚合窗口、保留天数。
/// </summary>
/// <remarks>
/// 页面本身不含任何业务逻辑：字段全部绑定到 <see cref="SettingsViewModel"/>，
/// 保存也走视图模型。数据来源与校验规则只有一份，换成别的调用方（比如托盘菜单）
/// 复用同一个视图模型即可。
/// </remarks>
public sealed partial class SettingsPage : Page
{
    private readonly SettingsViewModel _viewModel;

    /// <summary>
    /// 创建设置页。
    /// </summary>
    /// <param name="viewModel">设置页绑定，由组合根注入。</param>
    /// <exception cref="ArgumentNullException"><paramref name="viewModel"/> 为 <see langword="null"/>。</exception>
    public SettingsPage(SettingsViewModel viewModel)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

        InitializeComponent();
        DataContext = _viewModel;
    }

    private async void SaveButton_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // 保存完成后组合根会重建消息管线，这里只需把结果呈现给用户。
        await _viewModel.SaveAsync();
    }
}