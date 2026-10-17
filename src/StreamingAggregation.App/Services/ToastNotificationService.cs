using System.Diagnostics;
using StreamingAggregation.Core.Abstractions;

namespace StreamingAggregation.App.Services;

/// <summary>通知通道的探测结果。</summary>
public enum NotificationCapability
{
    /// <summary>尚未探测。</summary>
    Unknown = 0,

    /// <summary>可用，通知会被真正投递。</summary>
    Ready,

    /// <summary>系统不支持应用通知（Windows 版本过低）。</summary>
    UnsupportedPlatform,

    /// <summary>进程处于提权状态；Windows 通知平台会静默丢弃提权进程的所有通知。</summary>
    Elevated,

    /// <summary>向通知平台注册失败（unpackaged 应用的 AUMID/COM 注册环节出错）。</summary>
    RegistrationFailed,

    /// <summary>用户在系统设置里关闭了本应用的通知。</summary>
    DisabledByUser,

    /// <summary>本应用的通知被单独禁用。</summary>
    DisabledForApplication,

    /// <summary>通知被组策略禁用。</summary>
    DisabledByPolicy,

    /// <summary>通知被应用清单声明禁用。</summary>
    DisabledByManifest,
}

/// <summary>通知相关日志的严重程度。</summary>
public enum NotificationLogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

/// <summary>
/// 通知日志写入回调：<c>(级别, 消息, 异常)</c>。
/// </summary>
/// <remarks>
/// 项目尚未引入统一日志设施，故先用委托留出接缝；默认实现写 <see cref="Trace"/>。
/// TODO(T16): 接入 <c>Microsoft.Extensions.Logging</c> 后替换默认实现即可，调用方无需改动。
/// </remarks>
public delegate void NotificationLogSink(NotificationLogLevel level, string message, Exception? exception);

/// <summary>
/// 通知通道的占位实现：<b>阶段 2 不发任何通知，只把降级原因记进日志</b>。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么现在是个桩。</b>WinUI 3 版本的实现依赖 Windows App SDK 的
/// <c>AppNotificationManager</c>，那是 <c>net10.0-windows10.0.19041.0</c> 上的 WinRT API，
/// 在 net48 上不存在（net48 没有 WinAppSDK），照抄必然编译不过。
/// 阶段 5 的正确做法是用 <c>CommunityToolkit.WinUI.Notifications</c> 的
/// <c>ToastNotificationManagerCompat</c>（+ <c>Microsoft.Windows.SDK.Contracts</c> 的 winmd 引用），
/// 这两个包本阶段已经挂进 csproj，阶段 5 只需换掉本类内部实现。
/// </para>
/// <para>
/// <b>探针结论（.tmp/net48-probe/REPORT.md 第 5 项，实测）</b>：
/// <list type="bullet">
/// <item><description><c>ToastNotificationManagerCompat.CreateToastNotifier()</c> 可用，
/// <c>Setting == Enabled</c>，<c>Show()</c> 成功，且首次调用会自动写
/// <c>HKCU\SOFTWARE\Classes\AppUserModelId\</c>（按 exe 路径），因此不需要管理员权限、
/// 不需要 Start 菜单快捷方式——零安装目标成立。</description></item>
/// <item><description><b>不要</b>直接调 <c>Windows.UI.Notifications.ToastNotificationManager</c>：
/// unpackaged 进程没有 AUMID，实测抛 <c>HRESULT 0x80070490</c>。
/// 定时 toast（<c>AddToSchedule</c>）在 net48 unpackaged 下同样不可用。</description></item>
/// </list>
/// </para>
/// <para>
/// <b>降级语义保持不变</b>：本类仍然实现 <see cref="INotificationService"/>，仍然不向调用方抛异常，
/// <see cref="Probe"/> 仍然给出明确结论（这里是 <see cref="NotificationCapability.RegistrationFailed"/>），
/// 因此管线侧的调用逻辑一行都不用改，阶段 5 换实现即插即用。
/// 通知是锦上添花，不能因为它把聚合主流程带崩。
/// </para>
/// <para>
/// TODO(阶段 5): 用 <c>ToastNotificationManagerCompat</c> 重写 <c>Send</c>，
/// 把 <see cref="NotificationTemplates"/> 的 payload 交给 <c>ToastNotification</c>；
/// 恢复 <c>AppNotificationBuilder.IsUrgentScenarioSupported()</c> 的 urgent 场景探测。
/// </para>
/// </remarks>
public sealed class ToastNotificationService : INotificationService, IDisposable
{
    /// <summary>阶段 2 的固定降级结论：注册从未发生，通道不可用。</summary>
    private const NotificationCapability Stage2Capability = NotificationCapability.RegistrationFailed;

    /// <summary>阶段 2 的降级说明。日志与 UI 上都应能一眼看出"通知没接"是已知状态而非异常。</summary>
    private const string Stage2Reason =
        "通知通道尚未迁移到 WPF/net48（阶段 5 处理），本阶段所有通知都会被跳过。";

    private readonly NotificationLogSink _log;
    private readonly object _gate = new();
    private bool _probed;
    private bool _disposed;

    /// <summary>
    /// 创建通知服务。
    /// </summary>
    /// <param name="log">日志写入回调；为 <see langword="null"/> 时使用 <see cref="Trace"/> 输出。</param>
    public ToastNotificationService(NotificationLogSink? log = null) => _log = log ?? DefaultLog;

    /// <summary>最近一次探测得到的通知通道能力。</summary>
    public NotificationCapability Capability
    {
        get
        {
            lock (_gate)
            {
                return _probed ? Stage2Capability : NotificationCapability.Unknown;
            }
        }
    }

    /// <inheritdoc />
    public void NotifyUrgent(string title, string message) => Skip("urgent", title, message);

    /// <inheritdoc />
    public void NotifyDeadline(string title, string message) => Skip("deadline", title, message);

    /// <summary>
    /// 主动触发一次能力探测，不必等到真的发通知。
    /// </summary>
    /// <remarks>
    /// 供启动阶段做通道自检：提前把不可用原因落到日志里，
    /// 免得用户第一次收到紧急事项时才发现通知根本没开。
    /// </remarks>
    /// <returns>探测结论。阶段 2 恒为 <see cref="NotificationCapability.RegistrationFailed"/>。</returns>
    public NotificationCapability Probe()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return NotificationCapability.RegistrationFailed;
            }

            if (_probed)
            {
                return Stage2Capability;
            }

            _probed = true;
        }

        _log(NotificationLogLevel.Warning, Stage2Reason, null);
        return Stage2Capability;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        // 阶段 2 从未注册通知通道，因此没有反注册动作；阶段 5 在此补 Unregister 等价清理。
    }

    /// <summary>跳过一条通知：只记日志，绝不向调用方抛出。</summary>
    /// <remarks>
    /// 降级原因由 <see cref="Probe"/> 在启动时记一条就够了，但「我明明触发了却什么都没弹」
    /// 是最容易被当成 bug 报上来的问题，所以每次跳过都留一条 Debug 级记录——
    /// 日志默认没有监听器（项目尚未引入日志框架），这行的成本目前是零。
    /// </remarks>
    /// <param name="kind">通知类别，仅用于日志前缀。</param>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    private void Skip(string kind, string? title, string? message)
    {
        // 只在首次探测时已经记过原因就不再刷屏；但首次发送仍然要留一条，
        // 因为「我发了通知但什么都没弹」是最容易被当成 bug 报上来的问题。
        _log(NotificationLogLevel.Debug, $"已跳过 {kind} 通知：{Summarize(title, message)}", null);
    }

    /// <summary>
    /// 日志用的标题摘要：截断后只留一行，避免把整段群消息写进日志。
    /// </summary>
    /// <remarks>
    /// 两个分支都走 <see cref="NotificationTemplates.NormalizeTitle"/>，因为它按 Unicode 标量截断，
    /// 不会把代理对从中间劈开；这里直接复用其结果，不再二次按码元截断。
    /// </remarks>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    /// <returns>摘要文本。</returns>
    private static string Summarize(string? title, string? message)
    {
        var text = NotificationTemplates.NormalizeTitle(title);
        return string.IsNullOrEmpty(text)
            ? NotificationTemplates.NormalizeTitle(message)
            : text;
    }

    /// <summary>默认日志出口：写 <see cref="Trace"/>。</summary>
    /// <param name="level">级别。</param>
    /// <param name="message">消息。</param>
    /// <param name="exception">异常。</param>
    private static void DefaultLog(NotificationLogLevel level, string message, Exception? exception)
    {
        var line = exception is null
            ? $"[Notification:{level}] {message}"
            : $"[Notification:{level}] {message} -> {exception.GetType().Name}: {exception.Message}";

        Trace.WriteLine(line);
    }
}