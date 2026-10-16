using System;
using Microsoft.UI.Xaml.Controls;
using StreamingAggregation.App.ViewModels;
using StreamingAggregation.Infrastructure.ViewModels;

namespace StreamingAggregation.App.Composition;

/// <summary>
/// 界面外壳上下文：主窗口与页面拿到的全部依赖。
/// </summary>
/// <remarks>
/// <para>
/// 组合根负责构造页面，但 <see cref="Frame.Navigate(Type, object)"/> 只能传"一个"参数。
/// 把视图模型与页面工厂打成一个上下文对象，页面就不会被迫写无参构造再事后赋值——
/// 依赖在构造那一刻就是完整的，构造里就能安全地绑定数据。
/// </para>
/// <para>
/// 本类型只承载依赖，不含行为；真正的组装在 <see cref="AppCompositionRoot"/>。
/// </para>
/// </remarks>
public sealed class AppShell
{
    /// <summary>
    /// 创建外壳上下文。
    /// </summary>
    /// <param name="connectionStatus">连接状态绑定。</param>
    /// <param name="settings">设置页绑定。</param>
    /// <param name="firstRun">首启状态绑定。</param>
    /// <param name="pageFactory">按页面类型构造页面的工厂。</param>
    /// <exception cref="ArgumentNullException">任一参数为 <see langword="null"/>。</exception>
    public AppShell(
        ConnectionStatusViewModel connectionStatus,
        SettingsViewModel settings,
        FirstRunState firstRun,
        Func<Type, Page> pageFactory)
    {
        ConnectionStatus = connectionStatus ?? throw new ArgumentNullException(nameof(connectionStatus));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        FirstRun = firstRun ?? throw new ArgumentNullException(nameof(firstRun));
        PageFactory = pageFactory ?? throw new ArgumentNullException(nameof(pageFactory));
    }

    /// <summary>连接状态绑定。</summary>
    public ConnectionStatusViewModel ConnectionStatus { get; }

    /// <summary>设置页绑定。</summary>
    public SettingsViewModel Settings { get; }

    /// <summary>首启状态绑定。</summary>
    public FirstRunState FirstRun { get; }

    /// <summary>按页面类型构造页面的工厂，由组合根提供。</summary>
    public Func<Type, Page> PageFactory { get; }
}