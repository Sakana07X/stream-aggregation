using System.Text.RegularExpressions;
using StreamingAggregation.Tests.Fixtures;

namespace StreamingAggregation.Tests.Composition;

/// <summary>
/// 源码断言：首启遮罩层必须真的存在且接线正确。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是源码断言</b>：<see cref="MainWindow"/> 是 WinUI 3 类型，测试宿主加载
/// <c>StreamingAggregation.App</c> 需要 XAML/WinRT 运行时（同
/// <see cref="CoreInterfaceCoverageTests"/> 的说明）。因此"遮罩层是否存在、按钮是否接线"
/// 退化为对 XAML 与代码后置文件的源码核对。
/// </para>
/// <para>
/// <b>这些断言各挡什么回归</b>：
/// <list type="bullet">
/// <item>从 XAML 里删掉 <c>WelcomeOverlay</c>（遮罩层消失，首启引导静默失效）；</item>
/// <item>删掉按钮的 <c>Click</c> 接线（按钮看得见但点了没反应）；</item>
/// <item>把 <c>LaterButton_Click</c> 里的 <c>DismissAsync</c> 调用删掉
/// （用户每次启动都被欢迎页挡住）；</item>
    /// <item>忘设窗口初始尺寸（尺寸由 WinUI 默认决定，遮罩层布局不可控）。</item>
/// 遮罩层布局不可控）。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class FirstRunOverlayTests
{
    /// <summary><c>MainWindow.xaml</c> 相对仓库根的路径。</summary>
    private const string MainWindowRelativePath = "src/StreamingAggregation.App/MainWindow.xaml";

    /// <summary><c>MainWindow.xaml.cs</c> 相对仓库根的路径。</summary>
    private const string MainWindowCodeBehindRelativePath =
        "src/StreamingAggregation.App/MainWindow.xaml.cs";

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

    /// <summary><c>MainWindow.xaml</c> 必须声明 <c>WelcomeOverlay</c>，且遮罩层在 NavigationView 之后声明（Z 轴在上层）。</summary>
    [Fact]
    public void MainWindowXamlDeclaresWelcomeOverlayAboveNavigationView()
    {
        var source = RepositorySourceFile.Read(MainWindowRelativePath);

        // WelcomeOverlay 必须存在
        Assert.True(
            Regex.IsMatch(source, @"x:Name=""WelcomeOverlay""", RegexOptions.CultureInvariant),
            "MainWindow.xaml 缺少 WelcomeOverlay，首启遮罩层不存在。");

        // 遮罩层必须在 NavigationView 之后声明（XAML 中后声明的元素在 Z 轴上层）
        var navViewIndex = source.IndexOf("<NavigationView", StringComparison.Ordinal);
        var overlayIndex = source.IndexOf("x:Name=\"WelcomeOverlay\"", StringComparison.Ordinal);

        Assert.True(navViewIndex >= 0, "MainWindow.xaml 缺少 NavigationView。");
        Assert.True(
            overlayIndex > navViewIndex,
            "WelcomeOverlay 必须在 NavigationView 之后声明，否则遮罩层会被导航栏挡住。");

        // 遮罩层默认隐藏
        Assert.True(
            Regex.IsMatch(source, @"x:Name=""WelcomeOverlay""[^>]*Visibility=""Collapsed""", RegexOptions.CultureInvariant),
            "WelcomeOverlay 默认必须是 Collapsed，非首启时不应遮挡界面。");

        // 遮罩层必须有标题
        Assert.True(
            Regex.IsMatch(source, @"Text=""欢迎使用流式聚合""", RegexOptions.CultureInvariant),
            "遮罩层缺少标题「欢迎使用流式聚合」。");

        // 遮罩层必须包含三步引导文案
        Assert.Contains("启动 SnowLuma", source, StringComparison.Ordinal);
        Assert.Contains("群白名单", source, StringComparison.Ordinal);
        Assert.Contains("Endpoint", source, StringComparison.Ordinal);

        // 两个按钮必须存在并接线
        Assert.True(
            Regex.IsMatch(source, @"x:Name=""StartConfigButton""[^>]*Click=""StartConfigButton_Click""", RegexOptions.CultureInvariant),
            "遮罩层缺少「开始配置」按钮或未接线 Click。");

        Assert.True(
            Regex.IsMatch(source, @"x:Name=""LaterButton""[^>]*Click=""LaterButton_Click""", RegexOptions.CultureInvariant),
            "遮罩层缺少「稍后再说」按钮或未接线 Click。");
    }

/// <summary>
    /// 窗口初始尺寸必须真的被设置，否则遮罩层布局不可控。
    /// </summary>
    /// <remarks>
    /// <b>为什么断言代码后置而不是 XAML</b>：WinUI 3 的 <c>Window</c> 没有
    /// <c>Width</c>/<c>Height</c> 属性。把它们写进 XAML，XAML 编译器会报
    /// WMC0011「Unknown member」并直接终止编译——而且 Windows App SDK 1.7 的编译器
    /// 在独立进程模式下会把这个错误吞掉，只留下一个没有任何说明的退出码 1。因此初始
    /// 尺寸只能在运行时经 <c>AppWindow.Resize</c> 设置。
    /// </remarks>
    [Fact]
    public void MainWindowSetsInitialWindowSize()
    {
        var xaml = RepositorySourceFile.Read(MainWindowRelativePath);

        Assert.False(
            Regex.IsMatch(xaml, @"<Window[^>]*\bWidth=", RegexOptions.CultureInvariant),
            "WinUI 3 的 Window 没有 Width 属性，写进 XAML 会让 XAML 编译失败（WMC0011）。");

        var codeBehind = RepositorySourceFile.Read(MainWindowCodeBehindRelativePath);

        Assert.True(
            Regex.IsMatch(codeBehind, @"\.Resize\(new\s+Windows\.Graphics\.SizeInt32\(", RegexOptions.CultureInvariant),
            "MainWindow 未调用 AppWindow.Resize，窗口尺寸由 WinUI 默认决定，遮罩层布局不可控。");

        Assert.True(
            Regex.IsMatch(codeBehind, @"InitialWindowWidth\s*=\s*\d+", RegexOptions.CultureInvariant)
            && Regex.IsMatch(codeBehind, @"InitialWindowHeight\s*=\s*\d+", RegexOptions.CultureInvariant),
            "MainWindow 未定义有效的 InitialWindowWidth/InitialWindowHeight。");
    }

    /// <summary>
    /// <c>NavView_Loaded</c> 必须根据 <c>FirstRun.IsFirstRun</c> 决定遮罩可见性与初始导航。
    /// </summary>
    [Fact]
    public void NavViewLoadBranchesOnFirstRunState()
    {
        var source = RepositorySourceFile.Read(MainWindowCodeBehindRelativePath);

        var body = RepositorySourceFile.ExtractMethodBody(source, "private void NavView_Loaded(");
        Assert.False(
            string.IsNullOrEmpty(body),
            "MainWindow.xaml.cs 中找不到 NavView_Loaded 方法体，无法核对首启分支。");

        Assert.True(
            Regex.IsMatch(body!, @"FirstRun\.IsFirstRun", RegexOptions.CultureInvariant),
            "NavView_Loaded 未判断 FirstRun.IsFirstRun，遮罩层不会按首启状态显示。");

        Assert.True(
            Regex.IsMatch(body!, @"WelcomeOverlay\.Visibility\s*=\s*Visibility\.Visible", RegexOptions.CultureInvariant),
            "首启时未显示 WelcomeOverlay，用户看不到引导。");

        Assert.True(
            Regex.IsMatch(body!, @"WelcomeOverlay\.Visibility\s*=\s*Visibility\.Collapsed", RegexOptions.CultureInvariant),
            "非首启时未隐藏 WelcomeOverlay，遮罩层会一直挡在界面上。");
    }

    /// <summary>
    /// 「开始配置」按钮必须隐藏遮罩层并跳转到设置页。
    /// </summary>
    [Fact]
    public void StartConfigButtonHidesOverlayAndNavigatesToSettings()
    {
        var source = RepositorySourceFile.Read(MainWindowCodeBehindRelativePath);

        var body = RepositorySourceFile.ExtractMethodBody(source, "private void StartConfigButton_Click(");
        Assert.False(
            string.IsNullOrEmpty(body),
            "MainWindow.xaml.cs 中找不到 StartConfigButton_Click 方法体，无法核对按钮行为。");

        Assert.True(
            Regex.IsMatch(body!, @"WelcomeOverlay\.Visibility\s*=\s*Visibility\.Collapsed", RegexOptions.CultureInvariant),
            "「开始配置」未隐藏遮罩层。");

        Assert.True(
            Regex.IsMatch(body!, @"NavView\.MenuItems\s*\[\s*3\s*\]", RegexOptions.CultureInvariant),
            "「开始配置」未跳转到设置页（MenuItems[3]）。");
    }

    /// <summary>
    /// 「稍后再说」按钮必须隐藏遮罩层、调用 <c>DismissAsync</c> 持久化标记，并跳转到摘要页。
    /// </summary>
    [Fact]
    public void LaterButtonHidesOverlayPersistsFlagAndNavigatesToDigest()
    {
        var source = RepositorySourceFile.Read(MainWindowCodeBehindRelativePath);

        var body = RepositorySourceFile.ExtractMethodBody(source, "private void LaterButton_Click(");
        Assert.False(
            string.IsNullOrEmpty(body),
            "MainWindow.xaml.cs 中找不到 LaterButton_Click 方法体，无法核对按钮行为。");

        Assert.True(
            Regex.IsMatch(body!, @"WelcomeOverlay\.Visibility\s*=\s*Visibility\.Collapsed", RegexOptions.CultureInvariant),
            "「稍后再说」未隐藏遮罩层。");

        Assert.True(
            Regex.IsMatch(body!, @"FirstRun\.DismissAsync\s*\(", RegexOptions.CultureInvariant),
            "「稍后再说」未调用 FirstRun.DismissAsync，二次启动仍会弹遮罩。");

        Assert.True(
            Regex.IsMatch(body!, @"NavView\.MenuItems\s*\[\s*0\s*\]", RegexOptions.CultureInvariant),
            "「稍后再说」未跳转到摘要页（MenuItems[0]）。");
    }
}
