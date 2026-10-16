using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StreamingAggregation.App.Composition;
using StreamingAggregation.App.Services;

namespace StreamingAggregation.App;

/// <summary>
/// 应用入口（WinUI 3 unpackaged）。
/// </summary>
/// <remarks>
/// <para>
/// 入口点由 XAML 标记编译器自动生成：<c>App.xaml</c> 被登记为 <c>ApplicationDefinition</c>，
/// 编译器会产出 <c>obj/.../App.g.i.cs</c>，其中包含 <c>Program.Main</c> 与 <c>InitializeComponent()</c>。
/// 因此本项目刻意不包含 <c>Program.cs</c>，也不声明任何 <c>Main</c> 方法——手写入口会与生成的入口冲突（CS0017）。
/// </para>
/// <para>
/// 本类只做三件事：建组合根、把启动委托出去、把致命错误呈现出来。
/// 对象如何组装、启动顺序如何编排，全部在 <see cref="AppCompositionRoot"/> 里。
/// </para>
/// <para>
/// <b>启动失败必须看得见</b>：启动是"连接先起、窗口后开"，因此迁移、读设置、建连接这些步骤
/// 全部发生在窗口出现之前，任一失败都会让 <see cref="ReportFatalErrorAsync"/> 拿不到
/// <see cref="AppCompositionRoot.MainWindowRoot"/>。而启动任务又是刻意不 await 的，
/// 异常一旦被吞掉，WinUI 的消息泵照常运行——用户看到的就是一个"任务管理器里有、
/// 桌面上什么都没有"的幽灵进程。因此这条路径必须有 Win32 兜底对话框，并且必须显式退出。
/// </para>
/// </remarks>
public partial class App : Application
{
    /// <summary>启动失败时的进程退出码；与"用户主动退出"（0）区分开，便于安装器与脚本识别。</summary>
    private const int FatalStartupExitCode = 1;

    private AppCompositionRoot? _root;

    /// <summary>组合根实例。</summary>
    public AppCompositionRoot? CompositionRoot => _root;

    /// <summary>
    /// 创建应用对象。
    /// </summary>
    public App() => InitializeComponent();

    /// <summary>
    /// 启动应用。
    /// </summary>
    /// <remarks>
    /// 启动本身是异步的（要先迁移数据库、读设置），但 WinUI 的入口是同步的，
    /// 因此这里只把任务挂出去；真正的开窗时机由 <see cref="AppCompositionRoot.StartAsync"/> 决定。
    /// 组合根的创建也在这层 <c>try</c> 里：它一旦抛错，<see cref="AppCompositionRoot.MainWindowRoot"/>
    /// 连取都取不到，只能走 Win32 兜底。
    /// </remarks>
    /// <param name="args">启动参数。</param>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var dispatcher = DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("当前线程没有 WinUI 调度队列，无法启动界面。");

            _root = new AppCompositionRoot(action => dispatcher.TryEnqueue(() => action()));

            // 刻意不 await：OnLaunched 必须尽快返回，否则窗口迟迟不会出现。
            // StartAsync 自带 catch 与上报，失败不会变成无人观察的任务异常。
            _ = StartAsync(_root);
        }
        catch (Exception ex)
        {
            // 不捕获的话这里的异常会被 WinUI 吞掉：没有组合根、没有窗口、也没有提示。
            Trace.WriteLine($"启动失败：{ex}");
            ReportFatalErrorAndExit(ex);
        }
    }

    /// <summary>
    /// 启动组合根，并把致命错误呈现给用户。
    /// </summary>
    /// <param name="root">组合根。</param>
    private static async Task StartAsync(AppCompositionRoot root)
    {
        try
        {
            await root.StartAsync();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"启动失败：{ex}");
            await ReportFatalErrorAsync(root, ex);
        }
    }

    /// <summary>
    /// 呈现致命错误，并保证失败之后不会留下无界面进程。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 有 <see cref="XamlRoot"/> 时走窗口内的 <see cref="ContentDialog"/>——这是常规路径，
    /// 此时窗口已经存在，即便对话框显示失败，进程也仍有界面可关，不必由这里强退。
    /// </para>
    /// <para>
    /// 没有 <see cref="XamlRoot"/> 时（绝大多数启动失败都落在这里：迁移失败、DPAPI 解密失败、
    /// 端点非法……窗口压根还没建）走 Win32 兜底并退出。绝不能只写一行 <see cref="Trace"/> 就返回：
    /// 启动任务是不 await 的，异常已经被吞掉，消息泵照常运行，结果就是一个用户看不见的常驻进程。
    /// </para>
    /// </remarks>
    /// <param name="root">组合根。</param>
    /// <param name="error">启动异常。</param>
    private static async Task ReportFatalErrorAsync(AppCompositionRoot root, Exception error)
    {
        if (root.MainWindowRoot is not { } xamlRoot)
        {
            ReportFatalErrorAndExit(error);
            return;
        }

        var detail = FatalErrorDialog.Describe(error);

        try
        {
            var dialog = new ContentDialog
            {
                Title = FatalErrorDialog.DefaultTitle,
                Content = detail,
                CloseButtonText = "确定",
                XamlRoot = xamlRoot,
            };

            await dialog.ShowAsync();
        }
        catch (Exception dialogError)
        {
            // ContentDialog 自己也会失败（窗口正在关闭、XamlRoot 已失效）。
            // 这时只写 Trace 就收工，等于用户什么都看不到——退到不依赖 XAML 的 Win32 通道。
            Trace.WriteLine($"错误对话框无法显示：{dialogError}");
            FatalErrorDialog.Show(FatalErrorDialog.DefaultTitle, detail);
        }
    }

    /// <summary>
    /// 没有可用 <see cref="XamlRoot"/> 时的最后手段：Win32 消息框 + 显式退出进程。
    /// </summary>
    /// <remarks>
    /// <see cref="FatalErrorDialog.Show"/> 保证不抛异常，因此这里之后的退出一定会执行到。
    /// 顺序不可颠倒：<see cref="Environment.Exit"/> 一旦调用，后面的语句都不再执行。
    /// </remarks>
    /// <param name="error">启动异常。</param>
    private static void ReportFatalErrorAndExit(Exception error)
    {
        var detail = FatalErrorDialog.Describe(error);

        Trace.WriteLine($"启动失败（无窗口兜底）：{error.GetType().Name}: {detail}");

        FatalErrorDialog.Show(FatalErrorDialog.DefaultTitle, detail);

        Environment.Exit(FatalStartupExitCode);
    }
}