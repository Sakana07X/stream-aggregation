using System;
using System.Collections.Generic;
using System.Windows;

namespace StreamingAggregation.App.Services;

/// <summary>
/// 关闭模式的静态配置点：决定关掉主窗口后进程是继续留在托盘，还是直接退出。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要可配置。</b>托盘常驻是默认诉求（关窗后仍要接收群消息并弹通知），
/// 于是 <see cref="ShutdownMode.OnExplicitShutdown"/> 是默认值——此时只有托盘「退出」
/// 能结束进程。但不是所有场景都想要常驻：无障碍用户可能一次也碰不到托盘，
/// 远程桌面会话关掉最后一个窗口时也希望进程跟着收工。这两种诉求只能靠运行期切换。
/// </para>
/// <para>
/// <b>优先级（先命中先用）。</b>
/// <list type="number">
/// <item><description>命令行开关 <c>--exit-on-window-close</c> / <c>--tray-resident</c>。</description></item>
/// <item><description>环境变量 <see cref="EnvironmentVariable"/>，值为
/// <see cref="LastWindowCloseValue"/> / <see cref="TrayResidentValue"/>。</description></item>
/// <item><description>都没有 → <see cref="ShutdownMode.OnExplicitShutdown"/>（托盘常驻）。</description></item>
/// </list>
/// 命令行优先于环境变量：显式启动参数比进程继承来的环境更贴近当次意图。
/// </para>
/// <para>
/// 解析纯函数化（命令行与环境变量读取都从参数注入），是为了不依赖真实进程环境即可复现；
/// 未识别的取值一律回落到默认模式并交给调用方记日志，不让拼错的开关让应用起不来。
/// </para>
/// </remarks>
internal static class ShutdownModeResolver
{
    /// <summary>命令行开关：关掉最后一个窗口即退出进程（不托盘常驻）。</summary>
    public const string ExitOnWindowCloseSwitch = "--exit-on-window-close";

    /// <summary>命令行开关：关掉窗口只隐藏，进程留在托盘。</summary>
    public const string TrayResidentSwitch = "--tray-resident";

    /// <summary>环境变量名；值为 <see cref="LastWindowCloseValue"/> 或 <see cref="TrayResidentValue"/>。</summary>
    public const string EnvironmentVariable = "STREAMINGAGGREGATION_SHUTDOWN";

    /// <summary><see cref="EnvironmentVariable"/> 的「关窗即退出」取值。</summary>
    public const string LastWindowCloseValue = "lastwindowclose";

    /// <summary><see cref="EnvironmentVariable"/> 的「托盘常驻」取值。</summary>
    public const string TrayResidentValue = "trayresident";

    /// <summary>
    /// 解析出本次进程使用的关闭模式。
    /// </summary>
    /// <param name="commandLineArgs">命令行参数；为 <see langword="null"/> 时视为空。</param>
    /// <param name="readEnvironmentVariable">
    /// 读取环境变量的委托；为 <see langword="null"/> 时不查环境变量。
    /// 生产环境传 <c>Environment.GetEnvironmentVariable</c>，测试可注入桩。
    /// </param>
    /// <returns>
    /// 关闭模式。命令行或环境变量命中即返回对应值；未识别或未提供时返回
    /// <see cref="ShutdownMode.OnExplicitShutdown"/>。
    /// </returns>
    public static ShutdownMode Resolve(
        IReadOnlyList<string>? commandLineArgs,
        Func<string, string?>? readEnvironmentVariable = null)
    {
        var fromCommandLine = ResolveSwitch(commandLineArgs);
        if (fromCommandLine is not null)
        {
            return fromCommandLine.Value;
        }

        return ResolveVariable(readEnvironmentVariable?.Invoke(EnvironmentVariable))
            ?? ShutdownMode.OnExplicitShutdown;
    }

    /// <summary>
    /// 从命令行参数里找关闭模式开关。带 <c>--</c> 之外的写法（<c>/</c>、大小写）一律按未命中处理。
    /// </summary>
    /// <param name="commandLineArgs">命令行参数。</param>
    /// <returns>命中的关闭模式；未命中为 <see langword="null"/>。</returns>
    private static ShutdownMode? ResolveSwitch(IReadOnlyList<string>? commandLineArgs)
    {
        if (commandLineArgs is null)
        {
            return null;
        }

        foreach (var arg in commandLineArgs)
        {
            if (Matches(arg, ExitOnWindowCloseSwitch))
            {
                return ShutdownMode.OnLastWindowClose;
            }

            if (Matches(arg, TrayResidentSwitch))
            {
                return ShutdownMode.OnExplicitShutdown;
            }
        }

        return null;
    }

    /// <summary>
    /// 把环境变量取值翻译成关闭模式。
    /// </summary>
    /// <param name="value">环境变量原始取值。</param>
    /// <returns>翻译结果；取值缺失或无法识别时为 <see langword="null"/>。</returns>
    private static ShutdownMode? ResolveVariable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        // 环境变量大小写不该有语义，命令行开关则严格按文档写法来。
        return value!.Trim().ToLowerInvariant() switch
        {
            LastWindowCloseValue => ShutdownMode.OnLastWindowClose,
            TrayResidentValue => ShutdownMode.OnExplicitShutdown,
            _ => null,
        };
    }

    /// <summary>命令行开关按序数比较：开关名本身是文档契约，不接受别名。</summary>
    /// <param name="arg">当前参数。</param>
    /// <param name="expected">期望的开关名。</param>
    /// <returns>完全一致返回 <see langword="true"/>。</returns>
    private static bool Matches(string? arg, string expected) =>
        string.Equals(arg, expected, StringComparison.Ordinal);
}