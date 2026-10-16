using System;
using System.Threading;
using System.Threading.Tasks;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Infrastructure.ViewModels;

namespace StreamingAggregation.App.ViewModels;

/// <summary>
/// 首启状态绑定：承载 <see cref="IsFirstRun"/> 标志，决定主窗口是否显示欢迎遮罩层。
/// </summary>
/// <remarks>
/// <para>
/// <b>两个判据</b>：
/// <list type="bullet">
/// <item><description>
/// <b>已保存过设置</b>：<c>ISettingsStore.HasStoredSettingsAsync</c> 检测
/// <c>app_settings</c> 表里是否有行。迁移只建表不插行，只有
/// <c>SaveAsync</c> 才会写入第一行——这是"用户从未配置过应用"的精确信号。
/// </description></item>
/// <item><description>
/// <b>已看过欢迎页</b>：用户在遮罩层点「稍后再说」后，
/// <see cref="DismissAsync"/> 调用 <c>MarkWelcomeDismissedAsync</c> 把标记写入
/// <c>app_settings.welcome_dismissed</c> 列。下次启动时即便设置行已存在
/// （由标记写入），遮罩层也不会再弹。
/// </description></item>
/// </list>
/// </para>
/// <para>
/// <b>为什么复用 app_settings 而不是新建 LocalSettings/文件</b>：
/// app_settings 是现成的持久化通道，标记跟着设置一起走，备份/迁移时不丢；
/// 且标记列 <c>welcome_dismissed</c> 不被 <c>SaveAsync</c> 的 UPSERT 覆盖，
/// 用户后续保存设置不会把标记冲掉。
/// </para>
/// <para>
/// <b>初始化时机</b>：<c>AppCompositionRoot.InitializeCoreAsync</c> 在 UI 线程上
/// 调用 <see cref="InitializeAsync"/>，确保 <see cref="IsFirstRun"/> 的变更通知
/// 能正确推送到界面。
/// </para>
/// </remarks>
public sealed class FirstRunState : ObservableObject
{
    private readonly ISettingsStore _settingsStore;

    private bool _isFirstRun;

    /// <summary>
    /// 创建首启状态绑定。
    /// </summary>
    /// <param name="settingsStore">设置存储。</param>
    /// <exception cref="ArgumentNullException"><paramref name="settingsStore"/> 为 <see langword="null"/>。</exception>
    public FirstRunState(ISettingsStore settingsStore)
    {
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
    }

    /// <summary>
    /// 是否首次运行（从未保存过设置）。<see langword="true"/> 时主窗口应显示欢迎遮罩层。
    /// </summary>
    public bool IsFirstRun
    {
        get => _isFirstRun;
        private set => SetProperty(ref _isFirstRun, value);
    }

    /// <summary>
    /// 从数据库读取首启状态并填充 <see cref="IsFirstRun"/>。
    /// </summary>
    /// <remarks>
    /// 必须在 UI 线程上调用：<see cref="IsFirstRun"/> 的变更通知会直接驱动
    /// 遮罩层的可见性。
    /// </remarks>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsFirstRun = !await _settingsStore.HasStoredSettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把"欢迎页已看过"写入数据库，并立即把 <see cref="IsFirstRun"/> 置为 <see langword="false"/>。
    /// </summary>
    /// <remarks>
    /// 用户在遮罩层点「稍后再说」时调用。标记持久化后，下次启动
    /// <see cref="InitializeAsync"/> 会读到 <see langword="false"/>，遮罩层不再自动弹出。
    /// </remarks>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task DismissAsync(CancellationToken cancellationToken = default)
    {
        await _settingsStore.MarkWelcomeDismissedAsync(cancellationToken).ConfigureAwait(false);
        IsFirstRun = false;
    }
}
