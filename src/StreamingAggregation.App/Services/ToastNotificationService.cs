using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
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
/// 基于 Windows App SDK <c>AppNotificationManager</c> 的 <see cref="INotificationService"/> 实现。
/// </summary>
/// <remarks>
/// <para>
/// <b>能力探测 + 优雅降级</b>：首次发通知前依次探测"系统是否支持 → 进程是否提权 → 能否注册
/// → 系统是否允许本应用发通知"。任何一环不通过都只记日志，并把 <see cref="Capability"/> 置为对应状态；
/// 之后 <see cref="NotifyUrgent"/>/<see cref="NotifyDeadline"/> 变成空操作。通知是锦上添花，
/// 不能因为它把聚合主流程带崩。
/// </para>
/// <para>
/// <b>unpackaged 与 AUMID</b>：本应用以 unpackaged 方式运行，没有包标识，而通知平台要求每条通知
/// 有归属的 AUMID，否则通知无处落地。<c>AppNotificationManager.Register()</c> 会为 unpackaged 应用
/// 完成这层 AUMID/COM 注册（AUMID 取 <see cref="NotificationTemplates.AppUserModelId"/>），
/// 注册失败即视为通道不可用。已知部分 self-contained 发布形态下 unpackaged 原生通知会失效，
/// 这正是必须"探测后降级"、而不是假定可用的原因。
/// </para>
/// <para>
/// <b>线程模型</b>：通知由 OneBot WebSocket 的后台线程触发。<c>Show</c> 本身线程安全、
/// 不需要 UI 线程，因此本类刻意不做 <c>DispatcherQueue</c> 封送，避免引入跨线程依赖。
/// </para>
/// </remarks>
public sealed class ToastNotificationService : INotificationService, IDisposable
{
    private readonly NotificationLogSink _log;
    private readonly Func<bool> _isElevated;
    private readonly object _gate = new();

    private int _capability = (int)NotificationCapability.Unknown;
    private bool _registered;
    private bool _disposed;

    /// <summary>系统是否支持 <c>scenario="urgent"</c>；探测一次后固定，探测失败按不支持处理。</summary>
    private volatile bool _urgentScenario;

    /// <summary>
    /// 创建通知服务。
    /// </summary>
    /// <param name="log">日志写入回调；为 <see langword="null"/> 时使用 <see cref="Trace"/> 输出。</param>
    /// <param name="isElevated">
    /// 进程是否提权的判定委托；为 <see langword="null"/> 时按当前线程令牌实时判断。抽成委托便于测试。
    /// </param>
    public ToastNotificationService(NotificationLogSink? log = null, Func<bool>? isElevated = null)
    {
        _log = log ?? DefaultLog;
        _isElevated = isElevated ?? DetectElevation;
    }

    /// <summary>最近一次探测得到的通知通道能力；<see cref="NotificationCapability.Unknown"/> 表示尚未探测。</summary>
    public NotificationCapability Capability => (NotificationCapability)Volatile.Read(ref _capability);

    /// <inheritdoc />
    public void NotifyUrgent(string title, string message) =>
        Send(
            "urgent",
            title,
            message,
            NotificationTemplates.CreateUrgent(title, message, sourceId: null, urgentScenario: _urgentScenario));

    /// <inheritdoc />
    public void NotifyDeadline(string title, string message) =>
        Send("deadline", title, message, NotificationTemplates.CreateDeadline(title, message));

    /// <summary>
    /// 主动触发一次能力探测，不必等到真的发通知。
    /// </summary>
    /// <remarks>
    /// 供启动阶段做通道自检：提前把不可用原因落到日志里，
    /// 免得用户第一次收到紧急事项时才发现通知根本没开。
    /// </remarks>
    /// <returns>探测结论。</returns>
    public NotificationCapability Probe() => EnsureReady();

    /// <inheritdoc />
    public void Dispose()
    {
        bool shouldUnregister;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            shouldUnregister = _registered && Capability == NotificationCapability.Ready;
            _registered = false;
        }

        // 探测阶段就判定不可用时无需反注册。
        if (!shouldUnregister)
        {
            return;
        }

        try
        {
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex)
        {
            _log(NotificationLogLevel.Warning, "反注册通知通道失败（已忽略）。", ex);
        }
    }

    /// <summary>发送一条通知；任何失败都只记日志，绝不向调用方抛出。</summary>
    private void Send(string kind, string title, string message, NotificationPayload payload)
    {
        if (EnsureReady() != NotificationCapability.Ready)
        {
            // 通道不可用时静默跳过：这是设计上的降级路径，不是错误。
            return;
        }

        try
        {
            var notification = new AppNotification(payload.Xml)
            {
                // 同组折叠：紧急事项可能短时间连发多条，不分组会把操作中心刷满。
                Group = payload.Group,
                Priority = MapPriority(payload.Priority),
            };

            if (payload.Tag is { } tag)
            {
                notification.Tag = tag;
            }

            AppNotificationManager.Default.Show(notification);
            _log(NotificationLogLevel.Debug, $"已发送 {kind} 通知：{Summarize(title, message)}", null);
        }
        catch (Exception ex)
        {
            // 部分场景（如 self-contained unpackaged 发布）下 Show 会抛 WinRT 异常。
            // 单条通知失败不应拖垮后续通知，这里只记日志并保持通道可用。
            _log(NotificationLogLevel.Error, $"发送 {kind} 通知失败：{Summarize(title, message)}", ex);
        }
    }

    private static AppNotificationPriority MapPriority(NotificationPriority priority) => priority switch
    {
        NotificationPriority.High => AppNotificationPriority.High,
        _ => AppNotificationPriority.Default,
    };

    /// <summary>
    /// 幂等地完成能力探测与平台注册。
    /// </summary>
    /// <remarks>
    /// 结论缓存在 <see cref="_capability"/>：失败结论不会在每次调用时重复探测（否则会持续刷日志），
    /// 成功结论则跳过重复 <c>Register()</c>。
    /// </remarks>
    private NotificationCapability EnsureReady()
    {
        var current = Capability;
        if (current != NotificationCapability.Unknown)
        {
            return current;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return NotificationCapability.RegistrationFailed;
            }

            var cached = (NotificationCapability)Volatile.Read(ref _capability);
            if (cached != NotificationCapability.Unknown)
            {
                return cached;
            }

            var detected = Detect();
            Volatile.Write(ref _capability, (int)detected);
            return detected;
        }
    }

    /// <summary>按顺序执行各项探测，全部通过才返回 <see cref="NotificationCapability.Ready"/>。</summary>
    private NotificationCapability Detect()
    {
        if (!IsPlatformSupported())
        {
            return Degrade(
                NotificationCapability.UnsupportedPlatform,
                "当前系统不支持应用通知，所有通知将被跳过。");
        }

        if (_isElevated())
        {
            return Degrade(
                NotificationCapability.Elevated,
                "进程处于管理员提权状态，Windows 通知平台会静默丢弃所有通知；请以普通用户运行。");
        }

        return TryRegister();
    }

    private bool IsPlatformSupported()
    {
        try
        {
            return AppNotificationManager.IsSupported();
        }
        catch (Exception ex)
        {
            _log(NotificationLogLevel.Warning, "查询应用通知支持状态失败，按不支持处理。", ex);
            return false;
        }
    }

    /// <summary>向通知平台注册自身；unpackaged 应用由此建立 AUMID 归属。</summary>
    private NotificationCapability TryRegister()
    {
        _urgentScenario = ProbeUrgentScenario();

        try
        {
            AppNotificationManager.Default.Register();
            _registered = true;
        }
        catch (Exception ex)
        {
            return Degrade(
                NotificationCapability.RegistrationFailed,
                $"注册通知通道失败（AUMID={NotificationTemplates.AppUserModelId}），所有通知将被跳过。",
                ex);
        }

        return ResolveSetting();
    }

    /// <summary>
    /// 探测系统是否支持 <c>scenario="urgent"</c>。
    /// </summary>
    /// <remarks>
    /// 该场景只有部分 Windows 11 构建支持；写进不受支持的 XML 会导致整条通知被丢弃，
    /// 因此必须先探测。探测本身失败时按"不支持"处理，退回默认场景。
    /// </remarks>
    private bool ProbeUrgentScenario()
    {
        try
        {
            return AppNotificationBuilder.IsUrgentScenarioSupported();
        }
        catch (Exception ex)
        {
            _log(NotificationLogLevel.Debug, "探测 urgent 通知场景失败，按不支持处理。", ex);
            return false;
        }
    }

    /// <summary>读取通知平台设置并翻译成能力状态。</summary>
    private NotificationCapability ResolveSetting()
    {
        AppNotificationSetting setting;

        try
        {
            setting = AppNotificationManager.Default.Setting;
        }
        catch (Exception ex)
        {
            // 读不到设置不代表一定被禁用，按允许处理，交给 Show 自行判定。
            _log(NotificationLogLevel.Warning, "读取通知设置失败，按允许处理。", ex);
            return NotificationCapability.Ready;
        }

        if (setting == AppNotificationSetting.Enabled)
        {
            _log(
                NotificationLogLevel.Info,
                $"通知通道就绪（AUMID={NotificationTemplates.AppUserModelId}）。",
                null);
            return NotificationCapability.Ready;
        }

        return Degrade(MapSetting(setting), DescribeSetting(setting));
    }

    private static NotificationCapability MapSetting(AppNotificationSetting setting) => setting switch
    {
        AppNotificationSetting.DisabledForUser => NotificationCapability.DisabledByUser,
        AppNotificationSetting.DisabledForApplication => NotificationCapability.DisabledForApplication,
        AppNotificationSetting.DisabledByGroupPolicy => NotificationCapability.DisabledByPolicy,
        AppNotificationSetting.DisabledByManifest => NotificationCapability.DisabledByManifest,
        _ => NotificationCapability.UnsupportedPlatform,
    };

    private static string DescribeSetting(AppNotificationSetting setting) => setting switch
    {
        AppNotificationSetting.DisabledForUser => "用户在系统通知设置里关闭了本应用的通知，所有通知将被跳过。",
        AppNotificationSetting.DisabledForApplication => "本应用的通知已被单独禁用，所有通知将被跳过。",
        AppNotificationSetting.DisabledByGroupPolicy => "通知被组策略禁用，所有通知将被跳过。",
        AppNotificationSetting.DisabledByManifest => "通知被应用清单声明禁用，所有通知将被跳过。",
        AppNotificationSetting.Unsupported => "当前系统不支持应用通知，所有通知将被跳过。",
        _ => $"通知不可用（Setting={setting}），所有通知将被跳过。",
    };

    /// <summary>记录降级原因并返回降级状态。</summary>
    private NotificationCapability Degrade(NotificationCapability capability, string message, Exception? ex = null)
    {
        _log(
            ex is null ? NotificationLogLevel.Warning : NotificationLogLevel.Error,
            message,
            ex);
        return capability;
    }

    /// <summary>判断当前进程是否处于提权（管理员）状态。</summary>
    private static bool DetectElevation()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            // 取不到身份信息时按"未提权"处理：不因为探测自身失败就禁用通知。
            return false;
        }
    }

    private static void DefaultLog(NotificationLogLevel level, string message, Exception? exception)
    {
        var line = exception is null
            ? $"[Notification:{level}] {message}"
            : $"[Notification:{level}] {message} -> {exception.GetType().Name}: {exception.Message}";

        Trace.WriteLine(line);
    }

    /// <summary>
    /// 日志用的标题摘要：截断后只留一行，避免把整段群消息写进日志。
    /// </summary>
    /// <remarks>
    /// 两个分支都走 <see cref="NotificationTemplates.NormalizeTitle"/>，因为它按 Unicode 标量截断，
    /// 不会把代理对从中间劈开；这里直接复用其结果，不再二次按码元截断。
    /// </remarks>
    private static string Summarize(string? title, string? message)
    {
        var text = NotificationTemplates.NormalizeTitle(title);
        return string.IsNullOrEmpty(text)
            ? NotificationTemplates.NormalizeTitle(message)
            : text;
    }
}