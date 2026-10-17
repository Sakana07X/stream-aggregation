using System.Text.RegularExpressions;
using StreamingAggregation.Tests.Fixtures;

namespace StreamingAggregation.Tests.Composition;

/// <summary>
/// 源码断言：启动早期的任何失败都必须被用户看见，且不得留下"没有界面的进程"。
/// </summary>
/// <remarks>
/// <para>
/// <b>要挡住的回归</b>：启动顺序是"迁移 → 读设置 → 探测通知 → 建连接 → 最后才开窗口"。
/// 前面任何一步抛异常时主窗口压根还不存在，而 <c>OnStartup</c> 里的启动任务又是刻意
/// 不 await 的——异常一旦被 <c>catch</c> 吞掉，WPF 的消息泵照常运行，
/// 用户看到的就是一个"进程在任务管理器里、但什么都没有"的幽灵应用。
/// 所以这两件事必须同时成立：<b>有兜底对话框</b>，且<b>兜底之后显式退出</b>。
/// </para>
/// <para>
/// <b>为什么用源码断言而不是跑行为</b>：同 <see cref="SettingsPageSecurityTests"/>——
/// <c>StreamingAggregation.App</c> 是 WPF 可执行程序集，测试宿主加载它需要 STA 线程与
/// WPF 消息泵，而"启动失败"这条路径本身要求"没有窗口"。
/// 真实行为只能在真机上手工验证（步骤见 PR 描述），这里退化成"源码里确实这么写"。
/// </para>
/// <para>
/// <b>阶段 2 迁移（WinUI 3 → WPF/net48）</b>：三条锚点随实现一起改名，
/// 断言的<b>意图</b>（失败必须可见 + 失败必须退出）一条没变：
/// <list type="bullet">
/// <item><description><c>OnLaunched</c> → <c>OnStartup</c>（WPF 的启动回调名）。</description></item>
/// <item><description><c>MainWindowRoot</c>/<c>XamlRoot</c> 判空分支整体消失：
/// WPF 的致命错误统一走 Win32 <c>MessageBox</c>，本来就不需要窗口。</description></item>
/// <item><description><c>ReportFatalErrorAndExit</c> → <c>ShowFatalErrorAndShutdown</c>：
/// 退出走 <c>Application.Shutdown(exitCode)</c>（能触发 <c>Exit</c> 事件让组合根收尾），
/// <c>Environment.Exit</c> 只作为「连 <c>Application.Current</c> 都没有」的兜底。</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class FatalStartupVisibilityTests
{
    /// <summary><c>App.xaml.cs</c> 相对仓库根的路径。</summary>
    private const string AppRelativePath = "src/StreamingAggregation.App/App.xaml.cs";

    /// <summary><c>FatalErrorDialog.cs</c> 相对仓库根的路径。</summary>
    private const string FatalErrorDialogRelativePath =
        "src/StreamingAggregation.App/Services/FatalErrorDialog.cs";

    /// <summary>兜底对话框标题。</summary>
    private const string DialogTitle = "流式聚合启动失败";

    /// <summary>
    /// 致命错误必须走一条<b>不依赖任何窗口</b>的路径，且这条路径没有"静默 return"。
    /// </summary>
    /// <remarks>
    /// WinUI 3 时代这里断言的是"判空 <c>MainWindowRoot</c> 后第一时间 <c>ReportFatalErrorAndExit</c>"，
    /// 用来锁死 <c>if (root.MainWindowRoot is null) { return; }</c> 这种缺陷形态。
    /// 迁到 WPF 后那条判空分支不再存在——Win32 <c>MessageBox</c> 不需要界面根，
    /// 于是等价的不变量变成：<b>App.xaml.cs 里不存在 <c>ContentDialog</c>/<c>XamlRoot</c>，
    /// 也不存在"只写 Trace 就 return"的裸逃逸</b>。
    /// </remarks>
    [Fact]
    public void FatalErrorPathNeedsNoWindowAndNeverReturnsSilently()
    {
        var source = RepositorySourceFile.Read(AppRelativePath);

        Assert.False(
            Regex.IsMatch(source, @"\bContentDialog\b", RegexOptions.CultureInvariant),
            "App.xaml.cs 仍在用 ContentDialog：它必须挂在某个 XamlRoot 上才能显示，" +
            "而启动早期这条路径上窗口恰恰还不存在。");

        Assert.False(
            Regex.IsMatch(source, @"\bXamlRoot\b", RegexOptions.CultureInvariant),
            "App.xaml.cs 仍引用 XamlRoot；WPF 版致命错误统一走 Win32 MessageBox，不需要界面根。");

        // 兜底上报必须被显式 await / 触发：写成 `catch { Trace...; return; }` 就是静默失败。
        Assert.False(
            Regex.IsMatch(source, @"catch\s*\(\s*Exception\s+\w+\s*\)\s*\{\s*\}\s*", RegexOptions.CultureInvariant),
            "App.xaml.cs 存在空的 catch：启动异常会被静默吞掉，消息泵照常运行，进程变成幽灵。");

        Assert.True(
            Regex.IsMatch(source, @"FatalErrorDialog\.Show\s*\(", RegexOptions.CultureInvariant),
            "App.xaml.cs 没有调用 FatalErrorDialog.Show，致命错误对用户不可见。");
    }

    /// <summary>
    /// 兜底路径必须先弹 Win32 对话框，再退出进程；且退出码为 1。
    /// </summary>
    /// <remarks>
    /// 顺序不能反：<c>Shutdown</c>/<c>Environment.Exit</c> 一旦执行，后面的语句全部不执行，
    /// 先退出就等于什么都没告诉用户。另外，<c>StartAsync</c> 是刻意不 await 的，
    /// 不显式退出就没有第二个能结束进程的机会。
    /// </remarks>
    [Fact]
    public void FatalFallbackShowsDialogBeforeExiting()
    {
        var source = RepositorySourceFile.Read(AppRelativePath);

        var body = RepositorySourceFile.ExtractMethodBody(
            source,
            "private static void ShowFatalErrorAndShutdown(");

        Assert.False(
            string.IsNullOrEmpty(body),
            "App.xaml.cs 中找不到 ShowFatalErrorAndShutdown 方法体，无法核对退出时机。");

        var showIndex = body!.IndexOf("FatalErrorDialog.Show(", StringComparison.Ordinal);
        var shutdownIndex = body.IndexOf("Shutdown(", StringComparison.Ordinal);
        var exitIndex = body.IndexOf("Environment.Exit(", StringComparison.Ordinal);

        Assert.True(showIndex >= 0, "兜底路径没有调用 FatalErrorDialog.Show，用户看不到任何提示。");
        Assert.True(
            shutdownIndex >= 0 || exitIndex >= 0,
            "兜底路径既没有 Application.Shutdown 也没有 Environment.Exit，会留下无界面常驻进程。");
        Assert.True(
            shutdownIndex < 0 || showIndex < shutdownIndex,
            "Shutdown 出现在 FatalErrorDialog.Show 之前：进程先退出，提示将永远看不到。");
        Assert.True(
            exitIndex < 0 || showIndex < exitIndex,
            "Environment.Exit 出现在 FatalErrorDialog.Show 之前：进程先退出，提示将永远看不到。");

        // 退出码必须是 1，便于安装器/脚本把"启动失败"与"用户主动退出"区分开。
        Assert.True(
            Regex.IsMatch(source, @"FatalStartupExitCode\s*=\s*1\s*;", RegexOptions.CultureInvariant),
            "启动失败的退出码必须是 1。");

        // 退出码必须真的传进 Shutdown，而不是 Shutdown() 之后靠 Environment.ExitCode 兜。
        Assert.True(
            Regex.IsMatch(source, @"Shutdown\s*\(\s*FatalStartupExitCode\s*\)", RegexOptions.CultureInvariant),
            "Application.Shutdown 没有带上 FatalStartupExitCode，启动失败的退出码会退化成 0。");
    }

    /// <summary>
    /// <c>OnStartup</c> 里的组合根创建也必须被包住：调度器缺失与构造失败都发生在
    /// 组合根 <c>StartAsync</c> 之前，漏掉它们等于多留一个静默失败点。
    /// </summary>
    /// <remarks>
    /// 【阶段 2 迁移】WinUI 的回调名是 <c>OnLaunched</c>，WPF 的对应物是 <c>OnStartup</c>。
    /// </remarks>
    [Fact]
    public void OnStartupRoutesEarlyFailuresToTheFatalFallback()
    {
        var source = RepositorySourceFile.Read(AppRelativePath);

        var body = RepositorySourceFile.ExtractMethodBody(source, "protected override void OnStartup(");

        Assert.False(
            string.IsNullOrEmpty(body),
            "App.xaml.cs 中找不到 OnStartup 方法体，无法核对启动早期失败的兜底。");

        Assert.True(
            Regex.IsMatch(
                body!,
                @"catch\s*\(\s*Exception\s+\w+\s*\)\s*\{[\s\S]*?ReportFatalErrorAndExitAsync\s*\(\s*ex\s*\)",
                RegexOptions.CultureInvariant),
            "OnStartup 没有把调度器/组合根创建阶段的异常交给兜底路径。");

        // 组合根必须建在 try 里：它一旦抛错，后面所有兜底接线都无从谈起。
        Assert.True(
            Regex.IsMatch(body!, @"try\s*\{[\s\S]*?new\s+AppCompositionRoot\s*\(", RegexOptions.CultureInvariant),
            "AppCompositionRoot 的构造不在 OnStartup 的 try 之内，构造期异常会逃逸成静默失败。");
    }

    /// <summary>
    /// <c>StartAsync</c> 的 catch 必须真的包住 <c>root.StartAsync()</c>，
    /// 并把异常交给兜底路径而不是只写 Trace。
    /// </summary>
    /// <remarks>
    /// 【阶段 2 迁移】兜底方法是实例方法（要经 <c>Dispatcher</c> 回到 UI 线程），
    /// 因此 <c>StartAsync</c> 也随之从 static 变成实例方法。
    /// </remarks>
    [Fact]
    public void StartAsyncCatchesAndReportsStartupFailures()
    {
        var source = RepositorySourceFile.Read(AppRelativePath);

        var body = RepositorySourceFile.ExtractMethodBody(source, "private async Task StartAsync(");

        Assert.False(
            string.IsNullOrEmpty(body),
            "App.xaml.cs 中找不到 StartAsync 方法体，无法核对启动异常的捕获范围。");

        var tryIndex = body!.IndexOf("try", StringComparison.Ordinal);
        var awaitedIndex = body.IndexOf("await root.StartAsync()", StringComparison.Ordinal);
        var catchIndex = body.IndexOf("catch", StringComparison.Ordinal);
        var reportIndex = body.IndexOf("await ReportFatalErrorAndExitAsync(", StringComparison.Ordinal);

        Assert.True(tryIndex >= 0, "StartAsync 没有 try。");
        Assert.True(awaitedIndex > tryIndex, "root.StartAsync() 不在 try 之内，启动异常会逃逸成无人观察的任务异常。");
        Assert.True(catchIndex > awaitedIndex, "catch 不在 root.StartAsync() 之后，捕获范围不覆盖启动本身。");
        Assert.True(reportIndex > catchIndex, "catch 没有把异常交给 ReportFatalErrorAndExitAsync 上报。");
    }

    /// <summary>
    /// 兜底对话框必须真的调 Win32 <c>MessageBoxW</c>，且调用被平台判定与 try/catch 包住。
    /// </summary>
    /// <remarks>
    /// <c>MessageBoxW</c> 不依赖 WPF/WinRT 对象图，因此"窗口还没建起来"时仍然可用——
    /// 这正是它存在的理由。反过来，任何 WPF 对话框都必须先有窗口承载，
    /// 在这条路径上永远显示不出来。
    /// <para>
    /// 【阶段 2 迁移】平台判定从 <c>OperatingSystem.IsWindows()</c>（.NET 5+）
    /// 换成 net48 上真实存在的 <c>RuntimeInformation.IsOSPlatform(OSPlatform.Windows)</c>。
    /// </para>
    /// </remarks>
    [Fact]
    public void FatalErrorDialogUsesGuardedWin32MessageBox()
    {
        var source = RepositorySourceFile.Read(FatalErrorDialogRelativePath);

        Assert.True(
            Regex.IsMatch(
                source,
                @"DllImport\(\s*""user32\.dll""[^)]*\)\]\s*(?:\[[^\]]*\]\s*)*private\s+static\s+extern\s+int\s+MessageBoxW\s*\(",
                RegexOptions.CultureInvariant | RegexOptions.Singleline),
            "FatalErrorDialog.cs 没有对 user32.dll 的 MessageBoxW 做 P/Invoke 声明。");

        // ExactSpelling 必须是 true：CharSet.Unicode 下默认的自动后缀探测会先找 MessageBoxWW。
        Assert.True(
            Regex.IsMatch(source, @"ExactSpelling\s*=\s*true", RegexOptions.CultureInvariant),
            "MessageBoxW 的 P/Invoke 声明缺少 ExactSpelling = true，可能解析到不存在的 MessageBoxWW。");

        var body = RepositorySourceFile.ExtractMethodBody(source, "public static void Show(");

        Assert.False(
            string.IsNullOrEmpty(body),
            "FatalErrorDialog.cs 中找不到 Show 方法体，无法核对 Win32 调用是否被兜住。");

        Assert.True(
            Regex.IsMatch(
                body!,
                @"RuntimeInformation\.IsOSPlatform\s*\(\s*OSPlatform\.Windows\s*\)",
                RegexOptions.CultureInvariant),
            "Show 没有按 RuntimeInformation.IsOSPlatform(OSPlatform.Windows) 降级，非 Windows 平台上会直接抛。");

        Assert.True(
            Regex.IsMatch(body!, @"catch\s*\(\s*Exception\s+\w+\s*\)", RegexOptions.CultureInvariant),
            "Show 没有 catch：兜底对话框自己抛异常会吃掉调用方的退出决策，重新变成静默失败。");
    }

    /// <summary>
    /// 对话框文本在显示前必须脱敏：URL 里的 user:password、密钥赋值、鉴权头、
    /// 常见 API Key 前缀、以及长不透明令牌都要打码。
    /// </summary>
    /// <remarks>
    /// 提示框会被用户截图、进 Issue、进工单；而本应用启动失败最常见的诱因之一
    /// 就是"DPAPI 密文来自别的机器"——这类错误消息天然会碰到密钥相关上下文。
    /// </remarks>
    [Fact]
    public void FatalErrorDialogRedactsSecretsBeforeDisplay()
    {
        var source = RepositorySourceFile.Read(FatalErrorDialogRelativePath);

        var showBody = RepositorySourceFile.ExtractMethodBody(source, "public static void Show(");
        var describeBody = RepositorySourceFile.ExtractMethodBody(source, "public static string Describe(");
        var sanitizeBody = RepositorySourceFile.ExtractMethodBody(source, "public static string Sanitize(");

        Assert.False(string.IsNullOrEmpty(showBody), "FatalErrorDialog.cs 中找不到 Show 方法体。");
        Assert.False(string.IsNullOrEmpty(describeBody), "FatalErrorDialog.cs 中找不到 Describe 方法体。");
        Assert.False(string.IsNullOrEmpty(sanitizeBody), "FatalErrorDialog.cs 中找不到 Sanitize 方法体。");

        // Show 与 Describe 都必须先过 Sanitize，Trace 也一样（Trace 会进调试输出与日志文件）。
        Assert.True(
            Regex.IsMatch(showBody!, @"Sanitize\s*\(", RegexOptions.CultureInvariant),
            "Show 没有调用 Sanitize，原始异常消息会原样上屏。");
        Assert.True(
            Regex.IsMatch(describeBody!, @"Sanitize\s*\(", RegexOptions.CultureInvariant),
            "Describe 没有调用 Sanitize，原始异常消息会原样上屏。");

        // 五类脱敏规则缺一不可。
        Assert.True(
            Regex.IsMatch(source, @"://.*?@", RegexOptions.CultureInvariant),
            "缺少 URL user:password@host 的脱敏规则。");

        foreach (var sensitiveKey in new[] { "password", "token", "secret", @"api[_-]?key" })
        {
            Assert.True(
                Regex.IsMatch(source, Regex.Escape(sensitiveKey), RegexOptions.CultureInvariant),
                $"密钥赋值脱敏规则没有覆盖 “{sensitiveKey}”。");
        }

        Assert.True(
            Regex.IsMatch(source, @"Bearer", RegexOptions.CultureInvariant),
            "缺少 Authorization: Bearer 的脱敏规则。");

        // 已知 API Key 前缀规则（OpenAI / GitHub / AWS 三家是本应用真会用到的）。
        var apiKeyRanges = RepositorySourceFile.FindCallRanges(source, "KnownApiKeyPattern = new(");
        Assert.False(apiKeyRanges.Count == 0, "缺少已知 API Key 前缀的脱敏规则。");

        var apiKeyRule = source[apiKeyRanges[0].Start..(apiKeyRanges[0].End + 1)];
        foreach (var vendor in new[] { "sk", "gh", "AKIA" })
        {
            Assert.True(
                Regex.IsMatch(apiKeyRule, vendor, RegexOptions.CultureInvariant),
                $"已知 API Key 前缀规则没有覆盖 “{vendor}” 前缀。");
        }

        Assert.True(
            Regex.IsMatch(source, @"\{40,\}", RegexOptions.CultureInvariant),
            "缺少长不透明令牌（长度 ≥ 40）的兜底脱敏规则。");

        Assert.True(
            Regex.IsMatch(sanitizeBody!, @"Replace\s*\(", RegexOptions.CultureInvariant),
            "Sanitize 没有逐条替换规则，密钥清洗形同虚设。");
    }

    /// <summary>
    /// 脱敏必须是"选择性的"：提示框要保留文件绝对路径，用户才能知道该去修哪个 db。
    /// </summary>
    /// <remarks>
    /// 这是本任务的刻意取舍——路径不脱敏，密钥脱敏。
    /// 因此规则里不允许出现"把看起来像路径的东西整段抹掉"的写法：
    /// 令牌规则的左右边界都不能把 <c>\</c>、<c>/</c>、<c>:</c> 算进去，
    /// 否则 <c>%LOCALAPPDATA%\...</c> 里的长目录名会被误伤。
    /// </remarks>
    [Fact]
    public void FatalErrorDialogKeepsAbsolutePathsForDiagnosis()
    {
        var source = RepositorySourceFile.Read(FatalErrorDialogRelativePath);

        var sanitizeBody = RepositorySourceFile.ExtractMethodBody(source, "public static string Sanitize(");
        Assert.False(string.IsNullOrEmpty(sanitizeBody), "FatalErrorDialog.cs 中找不到 Sanitize 方法体。");

        // 规则的实参括号里含 {40,} 这类量词，不能用大括号配对来切片段，
        // 改按 new( ... ) 的圆括号配对取出整条规则。
        var ruleRanges = RepositorySourceFile.FindCallRanges(source, "LongOpaqueTokenPattern = new(");

        Assert.False(
            ruleRanges.Count == 0,
            "FatalErrorDialog.cs 中找不到长不透明令牌规则，无法核对它是否会误伤文件路径。");

        var (start, end) = ruleRanges[0];
        var tokenRule = source[start..(end + 1)];

        // 令牌规则的正则里左右边界都把 \ / : 排除在外，
        // 长目录名（如某些应用的包目录）就不会被当成令牌抹掉。
        Assert.True(
            Regex.IsMatch(tokenRule, @"\(\?<!\[[^\]]*\\\\", RegexOptions.CultureInvariant),
            "长令牌规则的左边界没有排除反斜杠，长目录名（如包目录）会被误脱敏。");

        Assert.True(
            Regex.IsMatch(tokenRule, @"\(\?!\[[^\]]*\\\\", RegexOptions.CultureInvariant),
            "长令牌规则的右边界没有排除反斜杠，长目录名（如包目录）会被误脱敏。");

        // 脱敏只允许把敏感片段换成掩码，绝不允许替换成空串——
        // 整体抹平会把"哪个 db 不可写"这条关键信息一起丢掉。
        Assert.True(
            Regex.IsMatch(source, @"\*\*\*", RegexOptions.CultureInvariant),
            "脱敏没有使用掩码标记 ***，无法确认敏感片段是被替换而不是被删除。");

        Assert.True(
            !Regex.IsMatch(sanitizeBody!, @"Replace\s*\([^)]*""\s*""\s*\)", RegexOptions.CultureInvariant),
            "Sanitize 存在替换成空串的规则，会把文件路径这类关键诊断信息一起丢掉。");
    }

    /// <summary>兜底对话框的标题必须与窗口内对话框一致。</summary>
    [Fact]
    public void FatalErrorDialogSharesTheInWindowDialogTitle()
    {
        var source = RepositorySourceFile.Read(FatalErrorDialogRelativePath);

        Assert.True(
            Regex.IsMatch(
                source,
                $@"DefaultTitle\s*=\s*""{Regex.Escape(DialogTitle)}""",
                RegexOptions.CultureInvariant),
            "FatalErrorDialog.DefaultTitle 与窗口内对话框标题不一致。");

        Assert.True(
            !Regex.IsMatch(
                source,
                @"(?<![A-Za-z0-9_])Title\s*=\s*""[^""]*启动失败""",
                RegexOptions.CultureInvariant),
            "ContentDialog 仍在硬编码标题，两条路径的文案会漂移。");
    }
}