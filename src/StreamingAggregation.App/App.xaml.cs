using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using StreamingAggregation.App.Composition;
using StreamingAggregation.App.Services;

namespace StreamingAggregation.App;

/// <summary>
/// 应用入口（WPF + .NET Framework 4.8，unpackaged）。
/// </summary>
/// <remarks>
/// <para>
/// 入口点由 XAML 标记编译器自动生成：<c>App.xaml</c> 被登记为 <c>ApplicationDefinition</c>，
/// 编译器会产出 <c>obj/…/net48/App.g.i.cs</c>，其中包含 <c>Program.Main</c>（带
/// <c>[STAThread]</c>）与 <c>InitializeComponent()</c>。因此本项目刻意不包含 <c>Program.cs</c>，
/// 也不声明任何 <c>Main</c> 方法——手写入口会与生成的入口冲突（CS0017）。
/// </para>
/// <para>
/// 本类只做四件事：定关闭模式、建组合根、把启动委托出去、把致命错误呈现出来。
/// 对象如何组装、启动顺序如何编排，全部在 <see cref="AppCompositionRoot"/> 里。
/// </para>
/// <para>
/// <b>启动失败必须看得见</b>：启动是"连接先起、窗口后开"，迁移、读设置、建连接这些步骤
/// 全部发生在窗口出现之前，而启动任务又是刻意不 await 的——异常一旦被吞掉，
/// WPF 的消息泵照常运行，用户看到的就是一个"任务管理器里有、桌面上什么都没有"的幽灵进程。
/// 因此这条路径统一走 <see cref="FatalErrorDialog"/>（纯 Win32 <c>MessageBoxW</c>，
/// 不依赖 XAML 对象图，在任何阶段都还活着）并强制退出进程。
/// </para>
/// <para>
/// <b>托盘常驻</b>：关闭模式默认 <see cref="ShutdownMode.OnExplicitShutdown"/>，
/// 关窗只隐藏、进程继续跑，退出改由托盘菜单或 <see cref="ShutdownModeResolver"/> 控制的
/// 「关窗即退出」模式决定。资源释放统一挂在 <see cref="Application.Exit"/> 上。
/// </para>
/// </remarks>
public partial class App : Application
{
    /// <summary>启动失败时的进程退出码；与"用户主动退出"（0）区分开，便于安装器与脚本识别。</summary>
    private const int FatalStartupExitCode = 1;

    private AppCompositionRoot? _root;

    /// <summary>组合根实例；<see cref="OnStartup"/> 失败时为 <see langword="null"/>。</summary>
    public AppCompositionRoot? CompositionRoot => _root;

    /// <summary>
    /// 创建应用对象。
    /// </summary>
    public App() => InitializeComponent();

    /// <summary>
    /// 启动应用。
    /// </summary>
    /// <remarks>
    /// 启动本身是异步的（要先迁移数据库、读设置），但 WPF 的入口是同步的，
    /// 因此这里只把任务挂出去；真正的开窗时机由 <see cref="AppCompositionRoot.StartAsync"/> 决定。
    /// 组合根的创建也在这层 <c>try</c> 里：它一旦抛错，后面的错误呈现与退出流程都无从谈起。
    /// </remarks>
    /// <param name="e">启动事件参数（携带命令行参数）。</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 先定关闭模式：MainWindow 的 Closing 拦截要不要生效，取决于它。
        ShutdownMode = ShutdownModeResolver.Resolve(e.Args, Environment.GetEnvironmentVariable);

        try
        {
            _root = new AppCompositionRoot(CreateUiMarshaller());
            Exit += OnExit;

            // 刻意不 await：OnStartup 必须尽快返回，否则窗口迟迟不会出现。
            // StartAsync 自带 catch 与上报，失败不会变成无人观察的任务异常。
            _ = StartAsync(_root);
        }
        catch (Exception ex)
        {
            // 不捕获的话这里的异常会被 WPF 吞掉：没有组合根、没有窗口、也没有提示。
            Trace.WriteLine($"启动失败：{ex}");
            _ = ReportFatalErrorAndExitAsync(ex);
        }
    }

    /// <summary>
    /// 构造「把动作搬到 UI 线程」的封送委托。
    /// </summary>
    /// <remarks>
    /// 语义刻意对齐 WinUI 的 <c>DispatcherQueue.TryEnqueue</c>：
    /// <list type="bullet">
    /// <item><description><b>永不内联</b>——即使已经在 UI 线程上，也要排队走一趟消息泵，
    /// 否则调用方会把"调用返回"误当成"已执行完"。</description></item>
    /// <item><description><b>调度器关闭后返回 <see langword="false"/></b>，
    /// 让调用方按自己的语义降级，而不是静默丢动作。</description></item>
    /// </list>
    /// 优先级固定 <see cref="DispatcherPriority.Normal"/>：不要用 <c>Send</c>，
    /// 那会让 UI 线程上的调用直接同步执行，上面的「永不内联」就破了。
    /// </remarks>
    /// <returns>封送委托。</returns>
    private Func<Action, bool> CreateUiMarshaller()
    {
        var dispatcher = Dispatcher;

        return action =>
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                return false;
            }

            try
            {
                dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
                return true;
            }
            catch (Exception ex)
            {
                // 关闭与 BeginInvoke 之间仍存在竞态窗口，异常必须吞在这里并如实汇报。
                Trace.WriteLine($"界面封送失败：{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        };
    }

    /// <summary>
    /// 启动组合根，并把致命错误呈现给用户。
    /// </summary>
    /// <param name="root">组合根。</param>
    private async Task StartAsync(AppCompositionRoot root)
    {
        try
        {
            await root.StartAsync();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"启动失败：{ex}");
            await ReportFatalErrorAndExitAsync(ex);
        }
    }

    /// <summary>
    /// 呈现致命错误，并保证失败之后不会留下无界面进程。
    /// </summary>
    /// <remarks>
    /// <b>为什么没有「窗口内对话框」这条常规路径</b>：走到这里的原因正是"主窗口还没建起来"，
    /// 而 WPF 里唯一不需要窗口的通用对话框就是 Win32 <c>MessageBox</c>——
    /// <see cref="FatalErrorDialog"/> 正是它。绝不能只写一行 <see cref="Trace"/> 就返回：
    /// 启动任务是不 await 的，异常已经被吞掉，消息泵照常运行，
    /// 结果就是一个用户看不见的常驻进程。
    /// </remarks>
    /// <param name="error">启动异常。</param>
    private async Task ReportFatalErrorAndExitAsync(Exception error)
    {
        var detail = FatalErrorDialog.Describe(error);
        Trace.WriteLine($"启动失败：{error.GetType().Name}: {detail}");

        try
        {
            // StartAsync 的续体跑在线程池上，Application.Shutdown 只允许 UI 线程调用。
            await Dispatcher.InvokeAsync(() => ShowFatalErrorAndShutdown(detail));
        }
        catch (Exception dispatchError)
        {
            // 调度器已经关掉了，连对话框都排不上队；退到不依赖消息泵的直连路径。
            Trace.WriteLine($"无法在 UI 线程上呈现致命错误（{dispatchError.GetType().Name}），退到直连路径。");
            FatalErrorDialog.Show(FatalErrorDialog.DefaultTitle, detail);
            Environment.Exit(FatalStartupExitCode);
        }
    }

    /// <summary>
    /// 在 UI 线程上显示致命错误对话框并终止进程。必须在 UI 线程调用。
    /// </summary>
    /// <param name="detail">已脱敏、已截断的错误描述。</param>
    private static void ShowFatalErrorAndShutdown(string detail)
    {
        // FatalErrorDialog.Show 保证不抛异常，因此后面的退出一定会执行到。
        FatalErrorDialog.Show(FatalErrorDialog.DefaultTitle, detail);

        var app = Application.Current;
        if (app is null)
        {
            Environment.Exit(FatalStartupExitCode);
            return;
        }

        // 走 Application.Shutdown 而不是 Environment.Exit：前者会先触发 Exit 事件，
        // 让组合根有机会停掉管线与托盘，不至于留下半开的数据库连接。
        app.Shutdown(FatalStartupExitCode);
    }

    /// <summary>
    /// 进程退出时释放组合根（会顺带停托盘、关窗、停管线）。
    /// </summary>
    /// <param name="sender">发送方。</param>
    /// <param name="e">退出事件参数。</param>
    private void OnExit(object sender, ExitEventArgs e)
    {
        Exit -= OnExit;

        _root?.Dispose();
        _root = null;
    }
}