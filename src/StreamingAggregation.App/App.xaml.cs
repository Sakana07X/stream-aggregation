using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using StreamingAggregation.App.Composition;

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
/// </remarks>
public partial class App : Application
{
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
    /// </remarks>
    /// <param name="args">启动参数。</param>
    /// <exception cref="InvalidOperationException">当前线程没有 WinUI 调度队列。</exception>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("当前线程没有 WinUI 调度队列，无法启动界面。");

        _root = new AppCompositionRoot(action => dispatcher.TryEnqueue(() => action()));

        // 刻意不 await：OnLaunched 必须尽快返回，否则窗口迟迟不会出现。
        _ = StartAsync(_root);
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
    /// 尽力弹出致命错误对话框。
    /// </summary>
    /// <remarks>
    /// 启动可能在窗口创建之前就失败，那时没有 <see cref="XamlRoot"/> 也就无法弹窗；
    /// 这种情况下只能靠 <see cref="Trace"/>，不吞异常、不假装成功。
    /// </remarks>
    /// <param name="root">组合根。</param>
    /// <param name="error">启动异常。</param>
    private static async Task ReportFatalErrorAsync(AppCompositionRoot root, Exception error)
    {
        if (root.MainWindowRoot is not { } xamlRoot)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "流式聚合启动失败",
            Content = error.Message,
            CloseButtonText = "确定",
            XamlRoot = xamlRoot,
        };

        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception dialogError)
        {
            Trace.WriteLine($"错误对话框无法显示：{dialogError.Message}");
        }
    }
}