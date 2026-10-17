using System.Collections.Generic;
using System.Text.RegularExpressions;
using StreamingAggregation.Tests.Fixtures;

namespace StreamingAggregation.Tests.Composition;

/// <summary>
/// 源码断言：主窗口壳、首启遮罩层与导航壳在当前阶段各自该做到哪一步。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是源码断言</b>：<see cref="MainWindow"/> 是 WPF 类型，测试宿主加载
/// <c>StreamingAggregation.App</c> 需要 STA 线程与 WPF 消息泵（同
/// <see cref="CoreInterfaceCoverageTests"/> 的说明）。因此"壳里有什么、遮罩层接线对不对"
/// 退化为对 XAML 与代码后置文件的源码核对。
/// </para>
/// <para>
/// <b>阶段 2 迁移（WinUI 3 → WPF/net48）后的分工</b>：
/// <list type="bullet">
/// <item><description>窗口标题、初始尺寸、关闭语义——<b>本阶段交付</b>，由本类断言锁死。</description></item>
/// <item><description>导航壳（WinUI <c>NavigationView</c> 在 WPF 里没有对应物）与
/// 首启遮罩层——<b>阶段 3/4 交付</b>；本阶段只要求"没有半迁移的 WinUI 残留"，
/// 由 <see cref="NoWinUiResidueRemainsInTheWindowShell"/> 守住。</description></item>
/// <item><description>遮罩层依赖的首启状态链路（<see cref="FirstRunState"/> → <c>AppShell</c> →
/// 组合根）—— <b>本阶段已经就绪</b>，遮罩层接上去时不必回头改这层。</description></item>
/// </list>
/// 阶段 3/4 落地时，请把 <see cref="NavigationShellAndWelcomeOverlayAreDeferred"/>
/// 一并替换回真实的遮罩层断言，不要让它留在"断言缺失"的状态上。
/// </para>
/// </remarks>
public sealed class FirstRunOverlayTests
{
    /// <summary><c>MainWindow.xaml</c> 相对仓库根的路径。</summary>
    private const string MainWindowRelativePath = "src/StreamingAggregation.App/MainWindow.xaml";

    /// <summary><c>MainWindow.xaml.cs</c> 相对仓库根的路径。</summary>
    private const string MainWindowCodeBehindRelativePath =
        "src/StreamingAggregation.App/MainWindow.xaml.cs";

    /// <summary><c>App.xaml</c> 相对仓库根的路径。</summary>
    private const string AppXamlRelativePath = "src/StreamingAggregation.App/App.xaml";

    /// <summary><c>App.xaml.cs</c> 相对仓库根的路径。</summary>
    private const string AppCodeBehindRelativePath = "src/StreamingAggregation.App/App.xaml.cs";

    /// <summary><c>ShutdownModeResolver.cs</c> 相对仓库根的路径。</summary>
    private const string ShutdownModeResolverRelativePath =
        "src/StreamingAggregation.App/Services/ShutdownModeResolver.cs";

    /// <summary><c>NavShell.xaml</c> 相对仓库根的路径。</summary>
    private const string NavShellRelativePath = "src/StreamingAggregation.App/Navigation/NavShell.xaml";

    /// <summary><c>NavShell.xaml.cs</c> 相对仓库根的路径。</summary>
    private const string NavShellCodeBehindRelativePath =
        "src/StreamingAggregation.App/Navigation/NavShell.xaml.cs";

    /// <summary><c>AppShell.cs</c> 相对仓库根的路径。</summary>
    private const string AppShellRelativePath = "src/StreamingAggregation.App/Composition/AppShell.cs";

    /// <summary><c>AppCompositionRoot.cs</c> 相对仓库根的路径。</summary>
    private const string CompositionRootPath = "src/StreamingAggregation.App/Composition/AppCompositionRoot.cs";

    /// <summary><c>FirstRunState.cs</c> 相对仓库根的路径。</summary>
    private const string FirstRunStateRelativePath = "src/StreamingAggregation.App/ViewModels/FirstRunState.cs";

    /// <summary><c>FirstRunState</c> 必须继承 <c>ObservableObject</c> 并暴露 <c>IsFirstRun</c> 属性。</summary>
    [Fact]
    public void FirstRunStateExposesIsFirstRunAsObservableProperty()
    {
        var source = RepositorySourceFile.Read(FirstRunStateRelativePath);

        Assert.True(
            Regex.IsMatch(source, @"class\s+FirstRunState\s*:\s*ObservableObject", RegexOptions.CultureInvariant),
            "FirstRunState 必须继承 ObservableObject，IsFirstRun 的变更通知才能驱动遮罩层可见性。");

        Assert.True(
            Regex.IsMatch(source, @"public\s+bool\s+IsFirstRun", RegexOptions.CultureInvariant),
            "FirstRunState 缺少 IsFirstRun 属性，主窗口无法判定是否显示遮罩层。");

        Assert.True(
            Regex.IsMatch(source, @"Task\s+DismissAsync\s*\(", RegexOptions.CultureInvariant),
            "FirstRunState 缺少 DismissAsync 方法，「稍后再说」后二次启动仍会弹遮罩。");
    }

    /// <summary><c>AppShell</c> 必须持有 <see cref="FirstRunState"/> 并拒绝 <see langword="null"/>。</summary>
    [Fact]
    public void AppShellExposesFirstRun()
    {
        var source = RepositorySourceFile.Read(AppShellRelativePath);

        Assert.True(
            Regex.IsMatch(source, @"public\s+FirstRunState\s+FirstRun\s*\{\s*get;\s*\}", RegexOptions.CultureInvariant),
            "AppShell 缺少 FirstRun 属性，MainWindow 拿不到首启状态。");

        Assert.True(
            Regex.IsMatch(source, @"FirstRun\s*=\s*firstRun\s*\?\?\s*throw\s+new\s+ArgumentNullException", RegexOptions.CultureInvariant),
            "AppShell 构造函数未拒绝 null FirstRun，首启状态可能静默失效。");
    }

    /// <summary>
    /// 组合根必须在初始化阶段创建 <see cref="FirstRunState"/>、调用 <c>InitializeAsync</c>，
    /// 并把它传给 <see cref="AppShell"/>。
    /// </summary>
    [Fact]
    public void CompositionRootWiresFirstRunIntoShell()
    {
        var source = RepositorySourceFile.Read(CompositionRootPath);

        var initializeBody = RepositorySourceFile.ExtractMethodBody(source, "private async Task InitializeCoreAsync(");
        Assert.False(
            string.IsNullOrEmpty(initializeBody),
            "AppCompositionRoot.InitializeCoreAsync 方法体不存在，无法核对首启状态初始化。");

        Assert.True(
            Regex.IsMatch(initializeBody!, @"new\s+FirstRunState\s*\(", RegexOptions.CultureInvariant),
            "InitializeCoreAsync 未创建 FirstRunState，首启状态永远不会初始化。");

        Assert.True(
            Regex.IsMatch(initializeBody!, @"RunOnUiAsync\s*\(\s*FirstRun\.InitializeAsync", RegexOptions.CultureInvariant),
            "InitializeCoreAsync 未在 UI 线程上调用 FirstRun.InitializeAsync，属性变更通知可能跨线程。");

        var showBody = RepositorySourceFile.ExtractMethodBody(source, "private void ShowMainWindow(");
        Assert.False(
            string.IsNullOrEmpty(showBody),
            "AppCompositionRoot.ShowMainWindow 方法体不存在，无法核对 FirstRun 注入。");

        Assert.True(
            Regex.IsMatch(showBody!, @"new\s+AppShell\s*\([^)]*FirstRun", RegexOptions.Singleline | RegexOptions.CultureInvariant),
            "ShowMainWindow 未把 FirstRun 传给 AppShell，主窗口拿不到首启状态。");
    }

    /// <summary>
    /// 窗口标题与初始尺寸必须都在 XAML/代码后置里各有唯一一处来源，且不再碰 WinAppSDK 的 <c>AppWindow</c>。
    /// </summary>
    /// <remarks>
    /// <b>为什么尺寸断言 XAML 而不是代码后置</b>（与 WinUI 时代相反）：
    /// WinUI 3 的 <c>Window</c> 没有 <c>Width</c>/<c>Height</c> 属性，写进 XAML 会被
    /// XAML 编译器判为 WMC0011「Unknown member」并终止编译，因此当时只能在运行期经
    /// <c>AppWindow.Resize</c> 设置。WPF 的 <c>Window</c> 是普通依赖属性，
    /// 直接写在 XAML 里就是唯一一处来源——也正因为如此，
    /// 「代码后置里不再出现 <c>AppWindow.Resize</c> / <c>ExtendsContentIntoTitleBar</c>」
    /// 才是一条值得锁死的断言：它们是迁移漏网的直接证据。
    /// <para>
    /// 标题则相反，刻意留在代码后置：它是 <see cref="MainWindow"/> 构造时的运行期事实，
    /// 写进 XAML 会和 <c>Shell</c> 的初始化顺序割裂。这条断言只要求<b>只有一处</b>，
    /// 两边都写迟早会漂移。
    /// </para>
    /// </remarks>
    [Fact]
    public void MainWindowDeclaresItsShellPropertiesInOnePlace()
    {
        var xaml = RepositorySourceFile.Read(MainWindowRelativePath);
        var codeBehind = RepositorySourceFile.Read(MainWindowCodeBehindRelativePath);

        // ── 初始尺寸：XAML 声明 ──
        var windowTag = Regex.Match(xaml, @"<Window\b[^>]*>", RegexOptions.CultureInvariant);
        Assert.True(windowTag.Success, "MainWindow.xaml 找不到 <Window> 根元素。");

        Assert.True(
            Regex.IsMatch(windowTag.Value, @"\bWidth\s*=\s*""1100""", RegexOptions.CultureInvariant),
            "MainWindow.xaml 的 <Window> 必须声明 Width=\"1100\"（设备无关像素）。");

        Assert.True(
            Regex.IsMatch(windowTag.Value, @"\bHeight\s*=\s*""720""", RegexOptions.CultureInvariant),
            "MainWindow.xaml 的 <Window> 必须声明 Height=\"720\"（设备无关像素）。");

        // ── 标题：代码后置声明，且只有这一处 ──
        Assert.True(
            Regex.IsMatch(codeBehind, @"Title\s*=\s*""流式聚合""", RegexOptions.CultureInvariant),
            "MainWindow.xaml.cs 没有把窗口标题设为「流式聚合」。");

        Assert.False(
            Regex.IsMatch(xaml, @"\bTitle\s*=", RegexOptions.CultureInvariant),
            "MainWindow.xaml 又声明了一遍 Title，与代码后置形成两处真相，迟早漂移。");

        // ── 不得残留 Windows App SDK 的窗口 API ──
        foreach (var winAppSdkCall in new[]
                 {
                     @"\.Resize\s*\(",
                     @"Windows\.Graphics\.SizeInt32",
                     @"ExtendsContentIntoTitleBar",
                     @"SetTitleBar\s*\(",
                     @"AppWindow",
                 })
        {
            Assert.False(
                Regex.IsMatch(codeBehind, winAppSdkCall, RegexOptions.CultureInvariant),
                $"MainWindow.xaml.cs 仍在使用 Windows App SDK 的 {winAppSdkCall}，" +
                "WPF 窗口尺寸与标题栏都由 XAML / 原生标题栏负责。");
        }
    }

    /// <summary>
    /// 窗口壳里不允许残留任何 WinUI 专有写法（阶段 2 的"没迁干净"检测）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 这是本阶段唯一一条<b>断言"不存在"</b>的测试，但它挡的是本次迁移最容易犯的错：
    /// 把 WinUI 的 XAML 原样留着、只改 C#。那样子编译能过（因为 <c>Pages\**</c> 已被排除），
    /// 却在阶段 3 打开编译时集中爆雷。
    /// </para>
    /// <para>
    /// 同时把"导航壳与遮罩层尚未交付"这件事钉成显式的 TODO 标记：
    /// 阶段 3/4 接手的人能一眼看出哪里还欠着，而不是去翻变更记录。
    /// </para>
    /// </remarks>
    [Fact]
    public void NoWinUiResidueRemainsInTheWindowShell()
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MainWindow.xaml"] = RepositorySourceFile.Read(MainWindowRelativePath),
            ["MainWindow.xaml.cs"] = RepositorySourceFile.Read(MainWindowCodeBehindRelativePath),
            ["App.xaml"] = RepositorySourceFile.Read(AppXamlRelativePath),
            ["App.xaml.cs"] = RepositorySourceFile.Read(AppCodeBehindRelativePath),
        };

        var forbidden = new[]
        {
            @"Microsoft\.UI\.",
            @"using:Microsoft\.UI",
            @"NavigationView",
            @"ThemeResource",
            @"XamlControlsResources",
            @"x:Bind",
            @"\bXamlRoot\b",
            @"Window\.AppWindow",
        };

        // KeyValuePair.Deconstruct 是 .NET Core 2.0 才有的，net48 上只能显式取 Key/Value。
        foreach (var file in sources)
        {
            // 注释必须先剥掉：迁移说明里本来就会提到「NavigationView 在 WPF 里没有对应物」
            // 这类旧名词，扫的是代码而不是注释。
            var code = StripComments(file.Value);

            foreach (var pattern in forbidden)
            {
                Assert.False(
                    Regex.IsMatch(code, pattern, RegexOptions.CultureInvariant),
                    $"{file.Key} 里仍残留 WinUI 专有写法 {pattern}：WPF/net48 下它没有对应物或根本编译不过。");
            }
        }
    }

    /// <summary>
    /// 剥掉 XML 注释、行注释与块注释，只留下会被编译的代码。
    /// </summary>
    /// <remarks>
    /// 行注释的识别要避开协议里的 <c>//</c>（<c>https://</c>）：只有前面不是 <c>:</c> 的
    /// <c>//</c> 才当注释起点。窗口壳这几个文件里没有带协议的字符串，
    /// 这条启发式够用；真要严谨就得上 Roslyn，那超出源码断言这条路的成本。
    /// </remarks>
    /// <param name="source">源码或 XAML 全文。</param>
    /// <returns>去掉注释后的文本。</returns>
    private static string StripComments(string source)
    {
        var stripped = Regex.Replace(source, @"<!--[\s\S]*?-->", " ", RegexOptions.CultureInvariant);
        stripped = Regex.Replace(stripped, @"/\*[\s\S]*?\*/", " ", RegexOptions.CultureInvariant);
        return Regex.Replace(stripped, @"(?<!:)//[^\r\n]*", " ", RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// 导航壳与首启遮罩层已交付：NavShell 存在、遮罩层可见性受控、按钮接线完整。
    /// </summary>
    [Fact]
    public void NavigationShellAndWelcomeOverlayAreWired()
    {
        var xaml = RepositorySourceFile.Read(MainWindowRelativePath);
        Assert.Contains("NavShell", xaml, StringComparison.Ordinal);

        var navXaml = RepositorySourceFile.Read(NavShellRelativePath);
        Assert.Contains("WelcomeOverlay", navXaml, StringComparison.Ordinal);
        Assert.Contains("开始配置", navXaml, StringComparison.Ordinal);
        Assert.Contains("稍后再说", navXaml, StringComparison.Ordinal);

        var navCodeBehind = RepositorySourceFile.Read(NavShellCodeBehindRelativePath);
        Assert.Contains("StartButton_Click", navCodeBehind, StringComparison.Ordinal);
        Assert.Contains("LaterButton_Click", navCodeBehind, StringComparison.Ordinal);

        var firstRunState = RepositorySourceFile.Read(FirstRunStateRelativePath);
        Assert.True(
            Regex.IsMatch(firstRunState, @"Task\s+DismissAsync\s*\(", RegexOptions.CultureInvariant),
            "FirstRunState.DismissAsync 缺失，「稍后再说」没有持久化入口。");
    }

    /// <summary>
    /// 关闭语义：托盘常驻模式下关窗只隐藏，「请求关闭」才真正关掉。
    /// </summary>
    /// <remarks>
    /// 这条是阶段 2 的核心验收项（关窗后进程仍在、托盘仍在；托盘「退出」才结束进程），
    /// 一旦 <c>Closing</c> 处理器被删掉或 <c>RequestClose</c> 被改名，
    /// "关窗即退出"与"关窗即隐藏"两条路会同时失效且没有任何编译信号。
    /// </remarks>
    [Fact]
    public void WindowCloseHidesInTrayResidentModeAndRequestCloseBypassesIt()
    {
        var codeBehind = RepositorySourceFile.Read(MainWindowCodeBehindRelativePath);

        var closingBody = RepositorySourceFile.ExtractMethodBody(codeBehind, "private void OnClosing(");
        Assert.False(
            string.IsNullOrEmpty(closingBody),
            "MainWindow.xaml.cs 中找不到 OnClosing 方法体，无法核对托盘常驻的关闭语义。");

        Assert.True(
            Regex.IsMatch(closingBody!, @"e\.Cancel\s*=\s*true", RegexOptions.CultureInvariant),
            "OnClosing 没有 e.Cancel = true：关窗会真的关掉窗口，进程可能被 ShutdownMode 带走。");

        Assert.True(
            Regex.IsMatch(closingBody!, @"\bHide\s*\(\s*\)", RegexOptions.CultureInvariant),
            "OnClosing 没有 Hide()：窗口只是被取消关闭，仍然留在屏幕上挡住界面。");

        Assert.True(
            Regex.IsMatch(
                closingBody!,
                @"ShutdownMode\s*==\s*ShutdownMode\.OnLastWindowClose",
                RegexOptions.CultureInvariant),
            "OnClosing 没有识别 ShutdownMode.OnLastWindowClose：「关窗即退出」模式会被常驻逻辑反向拦截。");

        var requestCloseBody =
            RepositorySourceFile.ExtractMethodBody(codeBehind, "public void RequestClose()");
        Assert.False(
            string.IsNullOrEmpty(requestCloseBody),
            "MainWindow.xaml.cs 中找不到 RequestClose 方法体：托盘「退出」将退化成普通关窗，进程永远退不出。");

        Assert.True(
            Regex.IsMatch(requestCloseBody!, @"_allowClose\s*=\s*true", RegexOptions.CultureInvariant),
            "RequestClose 没有置 _allowClose = true，仍然会被 OnClosing 拦截成隐藏。");
    }

    /// <summary>
    /// 关闭模式必须可配置：XAML 不写死，<c>OnStartup</c> 委托给 <c>ShutdownModeResolver</c>。
    /// </summary>
    /// <remarks>
    /// 托盘常驻（<c>OnExplicitShutdown</c>）是默认诉求，但「关窗即退出」对无障碍用户与
    /// 远程桌面场景同样合理，因此这条配置点不能被 XAML 的一个属性写死。
    /// </remarks>
    [Fact]
    public void ShutdownModeIsConfigurableRatherThanHardCoded()
    {
        var appXaml = RepositorySourceFile.Read(AppXamlRelativePath);
        var appCodeBehind = RepositorySourceFile.Read(AppCodeBehindRelativePath);
        var resolver = RepositorySourceFile.Read(ShutdownModeResolverRelativePath);

        Assert.False(
            Regex.IsMatch(appXaml, @"ShutdownMode\s*=", RegexOptions.CultureInvariant),
            "App.xaml 把 ShutdownMode 写死了，运行期（命令行/环境变量）就再也切不过去。");

        Assert.True(
            Regex.IsMatch(
                appCodeBehind,
                @"ShutdownMode\s*=\s*ShutdownModeResolver\.Resolve\s*\(",
                RegexOptions.CultureInvariant),
            "App.OnStartup 没有把 ShutdownMode 交给 ShutdownModeResolver 决定。");

        // 默认值必须仍是托盘常驻，且两处入口（命令行 / 环境变量）都在。
        Assert.True(
            Regex.IsMatch(
                resolver,
                @"\?\?\s*ShutdownMode\.OnExplicitShutdown",
                RegexOptions.CultureInvariant),
            "ShutdownModeResolver 的默认值不是 OnExplicitShutdown，关窗后进程不会留在托盘。");

        Assert.True(
            Regex.IsMatch(resolver, @"--exit-on-window-close", RegexOptions.CultureInvariant),
            "ShutdownModeResolver 缺少「关窗即退出」命令行开关。");

        Assert.True(
            Regex.IsMatch(
                resolver,
                @"ShutdownMode\.OnLastWindowClose",
                RegexOptions.CultureInvariant),
            "ShutdownModeResolver 从未返回 OnLastWindowClose，配置入口形同虚设。");

        Assert.True(
            Regex.IsMatch(resolver, @"STREAMINGAGGREGATION_SHUTDOWN", RegexOptions.CultureInvariant),
            "ShutdownModeResolver 缺少环境变量入口；托盘常驻应用常由外部脚本拉起，只能靠环境变量。");
    }
}
