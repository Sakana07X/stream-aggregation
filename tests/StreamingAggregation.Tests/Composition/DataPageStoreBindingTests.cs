using System.Text.RegularExpressions;
using StreamingAggregation.Tests.Fixtures;

namespace StreamingAggregation.Tests.Composition;

/// <summary>
/// 源码断言：摘要页、待办页、消息页必须真的读存储、走 UI 线程封送通道，而不是继续绑空集合。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是源码断言</b>：这三个页面是界面层类型，测试宿主加载
/// <c>StreamingAggregation.App</c> 需要 STA 线程与 WPF 消息泵（同
/// <see cref="CoreInterfaceCoverageTests"/> 的说明）。于是"首屏有没有接上真实数据"
/// 只能核对页面代码本身写了什么。
/// </para>
/// <para>
/// <b>这些断言各挡什么回归</b>：
/// <list type="bullet">
/// <item>把 <c>ItemsSource = Array.Empty&lt;T&gt;()</c> 写回去（页面看起来正常、永远没数据）；
/// </item>
/// <item>在 <c>await</c> 之后绕过封送通道直接给 <c>ItemsSource</c> 赋值——
/// 编译得过、运行必崩（<c>RPC_E_WRONG_THREAD</c>），是最难在开发机上复现的一类崩法；
/// </item>
/// <item>忘了往组合根的 <c>CreatePage</c> 里注入存储，页面静默退化成空列表
/// （阶段 2 见 <see cref="CompositionRootDefersPageAssemblyUntilPagesAreMigrated"/>）；
/// </item>
/// <item>待办页的完成/忽略按钮又变回空实现。
/// </item>
/// </list>
/// </para>
/// <para>
/// <b>阶段 2 状态</b>：这三个页面连同 <c>SettingsPage</c> 仍是 WinUI XAML，
/// 已被 csproj 排除出编译，因此本类里对页面代码后置的断言暂时核对的是
/// <b>仓库里保留的 WinUI 源码</b>，而不是运行中的行为。等阶段 3/4 把它们迁成 WPF，
/// 这些断言会原样重新生效（页面类型名、存储接口、封送调用前缀都不需要改）。
/// </para>
/// </remarks>
public sealed class DataPageStoreBindingTests
{
    /// <summary>页面类型名 → 页面代码后置文件相对仓库根的路径。</summary>
    private const string PagesDirectory = "src/StreamingAggregation.App/Pages/";

    /// <summary>页面必须借道 UI 线程封送的那次调用的前缀。</summary>
    private const string MarshalCallPrefix = "_load.Apply(";

    /// <summary>组合根里页面工厂所在方法的签名。</summary>
    private const string CreatePageSignature = "private Page CreatePage(Type pageType)";

    /// <summary>组合根源文件相对仓库根的路径。</summary>
    private const string CompositionRootPath = "src/StreamingAggregation.App/Composition/AppCompositionRoot.cs";

    /// <summary>页面加载上下文源文件相对仓库根的路径。</summary>
    private const string PageLoadContextPath = "src/StreamingAggregation.App/Services/PageLoadContext.cs";

    /// <summary>
    /// 每个数据页的代码后置文件。
    /// </summary>
    public static TheoryData<string> DataPagePaths =>
    [
        PagesDirectory + "DigestPage.xaml.cs",
        PagesDirectory + "TodoPage.xaml.cs",
        PagesDirectory + "MessagesPage.xaml.cs",
    ];

    /// <summary>
    /// 每个数据页对应的存储接口（按 <see cref="DataPagePaths"/> 同序）。
    /// </summary>
    public static TheoryData<string, string> DataPageStoreInterfaces
    {
        get
        {
            var data = new TheoryData<string, string>();
            data.Add(PagesDirectory + "DigestPage.xaml.cs", "IDigestStore");
            data.Add(PagesDirectory + "TodoPage.xaml.cs", "ITaskStore");
            data.Add(PagesDirectory + "MessagesPage.xaml.cs", "IMessageStore");
            return data;
        }
    }

    /// <summary>
    /// 页面必须以字段形式持有注入的存储，并在构造时拒绝 <see langword="null"/>。
    /// </summary>
    /// <param name="pagePath">页面代码后置文件相对仓库根的路径。</param>
    /// <param name="storeInterface">该页面应使用的存储接口名。</param>
    [Theory]
    [MemberData(nameof(DataPageStoreInterfaces))]
    public void EveryDataPageHoldsAnInjectedStoreAndRejectsNull(string pagePath, string storeInterface)
    {
        var source = RepositorySourceFile.Read(pagePath);
        var pageName = PageName(pagePath);

        Assert.True(
            Regex.IsMatch(
                source,
                $@"private\s+readonly\s+{Regex.Escape(storeInterface)}\s+_\w+",
                RegexOptions.CultureInvariant),
            $"{pageName}.xaml.cs 没有把 {storeInterface} 存成只读字段，页面拿不到真实数据。");

        // 依赖必须在构造那一刻就位：迟到的 null 赋值会让 Loaded 处理器在运行时炸空引用。
        Assert.True(
            source.Contains($"public {pageName}({storeInterface}", StringComparison.Ordinal),
            $"{pageName}.xaml.cs 的构造函数没有声明 {storeInterface} 参数，页面只能拿到空数据。");

        var constructor = RepositorySourceFile.ExtractMethodBody(source, $"public {pageName}({storeInterface}");
        Assert.False(
            string.IsNullOrEmpty(constructor),
            $"{pageName}.xaml.cs 找不到构造函数方法体，无法核对依赖校验。");
        Assert.True(
            Regex.IsMatch(constructor!, @"ArgumentNullException\s*\(", RegexOptions.CultureInvariant),
            $"{pageName} 的构造函数没有校验 {storeInterface} 为 null。");
    }

    /// <summary>
    /// 每一处 <c>ItemsSource</c> 赋值都必须发生在 UI 线程封送调用内部。
    /// </summary>
    /// <remarks>
    /// 这是本文件最要紧的一条。存储读取在线程池上继续，<c>await</c> 之后直接赋值会把
    /// WinUI 对象改在错误的线程上，运行必崩且很难在开发机上稳定复现。
    /// </remarks>
    /// <param name="pagePath">页面代码后置文件相对仓库根的路径。</param>
    [Theory]
    [MemberData(nameof(DataPagePaths))]
    public void EveryItemsSourceAssignmentGoesThroughTheUiMarshalChannel(string pagePath)
    {
        var source = RepositorySourceFile.Read(pagePath);
        var pageName = PageName(pagePath);

        var marshalRanges = RepositorySourceFile.FindCallRanges(source, MarshalCallPrefix);
        Assert.NotEmpty(marshalRanges);

        var assignments = Regex.Matches(source, @"\bItemsSource\s*=");
        Assert.NotEmpty(assignments);

        foreach (Match assignment in assignments)
        {
            Assert.True(
                marshalRanges.Any(range => assignment.Index > range.Start && assignment.Index < range.End),
                $"{pageName}.xaml.cs 在 {MarshalCallPrefix} 之外直接给 ItemsSource 赋值" +
                $"（字符偏移 {assignment.Index}）：异步加载会在非 UI 线程上改 WinUI 对象，运行时会抛 RPC_E_WRONG_THREAD。");
        }
    }

    /// <summary>
    /// 存储调用必须显式退出同步上下文，让"回到 UI 线程"只由封送通道负责。
    /// </summary>
    /// <param name="pagePath">页面代码后置文件相对仓库根的路径。</param>
    [Theory]
    [MemberData(nameof(DataPagePaths))]
    public void EveryDataPageOptsOutOfTheSynchronizationContextOnStoreCalls(string pagePath)
    {
        var source = RepositorySourceFile.Read(pagePath);

        Assert.True(
            source.Contains("ConfigureAwait(false)", StringComparison.Ordinal),
            $"{PageName(pagePath)}.xaml.cs 的存储调用没有 ConfigureAwait(false)，" +
            "线程归属会隐式依赖 WinUI 的 SynchronizationContext，与显式封送的写法相互掩盖。");
    }

    /// <summary>
    /// 页面不能再绑空集合占位。
    /// </summary>
    /// <param name="pagePath">页面代码后置文件相对仓库根的路径。</param>
    [Theory]
    [MemberData(nameof(DataPagePaths))]
    public void NoDataPageStillBindsPlaceholderCollections(string pagePath)
    {
        var source = RepositorySourceFile.Read(pagePath);
        var pageName = PageName(pagePath);

        Assert.False(
            Regex.IsMatch(source, @"ItemsSource\s*=\s*Array\.Empty", RegexOptions.CultureInvariant),
            $"{pageName}.xaml.cs 又把空集合直接绑到了 ItemsSource 上，页面会一直显示为空。");

        Assert.False(
            source.Contains("空集合占位", StringComparison.Ordinal),
            $"{pageName}.xaml.cs 仍留着“空集合占位”的旧实现。");

        Assert.False(
            Regex.IsMatch(source, @"TODO\(T1[0-9]\+\)", RegexOptions.CultureInvariant),
            $"{pageName}.xaml.cs 仍有 TODO：待办/忽略之类的接线没有完成。");
    }

    /// <summary>
    /// 每个数据页的刷新按钮都必须在 XAML 与代码后置之间对得上号。
    /// </summary>
    /// <remarks>
    /// XAML 里的事件名是运行时按名字解析的，写错不会编译失败，只会在点按钮时静默无反应。
    /// </remarks>
    /// <param name="pagePath">页面代码后置文件相对仓库根的路径。</param>
    [Theory]
    [MemberData(nameof(DataPagePaths))]
    public void EveryDataPageExposesAWiredRefreshButton(string pagePath)
    {
        var pageName = PageName(pagePath);
        var xaml = RepositorySourceFile.Read($"{PagesDirectory}{pageName}.xaml");
        var code = RepositorySourceFile.Read(pagePath);

        Assert.Contains(
            @"Click=""RefreshButton_Click""",
            xaml,
            StringComparison.Ordinal);
        Assert.True(
            Regex.IsMatch(code, @"void\s+RefreshButton_Click\s*\(\s*object\s+sender", RegexOptions.CultureInvariant),
            $"{pageName}.xaml.cs 缺少 RefreshButton_Click 处理器，XAML 上的刷新按钮点了不会有反应。");
    }

    /// <summary>摘要页必须按它实际展示的四个分组分别调用分类查询。</summary>
    [Fact]
    public void DigestPageLoadsEveryCategoryItDisplays()
    {
        var source = RepositorySourceFile.Read(PagesDirectory + "DigestPage.xaml.cs");

        Assert.True(
            Regex.IsMatch(source, @"_digestStore\s*\r?\n?\s*\.GetByCategoryAsync\s*\(", RegexOptions.CultureInvariant),
            "DigestPage.xaml.cs 没有通过注入的 IDigestStore 调用 GetByCategoryAsync。");

        // XAML 里的四个分组标题（紧急/待办/学习/普通）必须与这里加载的四个分类一一对应，
        // 否则会出现"某一段永远是空的"。
        foreach (var category in new[] { "Urgent", "Todo", "Study", "Normal" })
        {
            Assert.True(
                Regex.IsMatch(source, $@"DigestCategory\.{category}\b", RegexOptions.CultureInvariant),
                $"DigestPage.xaml.cs 没有加载 DigestCategory.{category}，页面对应分组会一直是空的。");
        }

        foreach (var listName in new[] { "UrgentList", "TodoList", "StudyList", "NormalList" })
        {
            Assert.True(
                Regex.IsMatch(source, $@"{listName}\.ItemsSource\s*=", RegexOptions.CultureInvariant),
                $"DigestPage.xaml.cs 没有给 {listName} 赋值。");
        }
    }

    /// <summary>待办页的列表、完成与忽略都必须落到 <c>ITaskStore</c> 上。</summary>
    [Fact]
    public void TodoPageWiresCompleteAndIgnoreToTheStore()
    {
        var source = RepositorySourceFile.Read(PagesDirectory + "TodoPage.xaml.cs");

        Assert.True(
            Regex.IsMatch(source, @"_taskStore\.GetPendingAsync\s*\(", RegexOptions.CultureInvariant),
            "TodoPage.xaml.cs 没有通过注入的 ITaskStore 调用 GetPendingAsync。");
        Assert.True(
            Regex.IsMatch(source, @"_taskStore\.SetCompletedAsync\s*\(\s*id\s*,\s*true", RegexOptions.CultureInvariant),
            "TodoPage 的完成操作没有调用 SetCompletedAsync(id, true)。");
        Assert.True(
            Regex.IsMatch(source, @"_taskStore\.SetIgnoredAsync\s*\(\s*id\s*,\s*true", RegexOptions.CultureInvariant),
            "TodoPage 的忽略操作没有调用 SetIgnoredAsync(id, true)。");

        // 按钮靠 Tag 传行动项 ID：XAML 与代码后置必须一致。
        var xaml = RepositorySourceFile.Read(PagesDirectory + "TodoPage.xaml");
        Assert.Contains(@"Tag=""{x:Bind Id}""", xaml, StringComparison.Ordinal);
        Assert.True(
            Regex.IsMatch(source, @"Tag:\s*string\s+id", RegexOptions.CultureInvariant),
            "TodoPage.xaml.cs 没有从按钮 Tag 上取行动项 ID，完成/忽略会找不到要改哪一行。");
    }

    /// <summary>消息页必须用最近消息查询撑起列表。</summary>
    [Fact]
    public void MessagesPageLoadsRecentMessages()
    {
        var source = RepositorySourceFile.Read(PagesDirectory + "MessagesPage.xaml.cs");

        Assert.True(
            Regex.IsMatch(source, @"_messageStore\s*\r?\n?\s*\.GetRecentAsync\s*\(", RegexOptions.CultureInvariant),
            "MessagesPage.xaml.cs 没有通过注入的 IMessageStore 调用 GetRecentAsync。");
        Assert.True(
            Regex.IsMatch(source, @"MessagesListView\.ItemsSource\s*=", RegexOptions.CultureInvariant),
            "MessagesPage.xaml.cs 没有给 MessagesListView 赋值。");
    }

    /// <summary>
    /// 页面工厂在页面尚未迁移到 WPF 的阶段必须<b>响亮地失败</b>，而不是悄悄返回空页面。
    /// </summary>
    /// <remarks>
/// <para>
/// <b>这条断言原本挡什么</b>：组合根漏注入存储时，页面静默退化成空列表——
/// 编译得过、界面不报错、用户只看到"永远没有数据"。因此原断言逐页核对
/// <c>CreatePage</c> 里的 <c>new DigestPage(_services.Resolve&lt;IDigestStore&gt;(), QueueOnUiThread)</c>。
/// </para>
/// <para>
/// <b>阶段 2 迁移后的等价不变量</b>：三个页面的 XAML 仍在用 WinUI 专有控件，
/// 已被 csproj 排除出编译，<c>CreatePage</c> 因此暂时不能构造它们。此时最危险的写法
/// 是让 <c>CreatePage</c> 返回 <see langword="null"/> 或某个空壳——那会让阶段 3 拿着
/// "看起来已完成"的组合根开工。所以改成断言：
/// <list type="number">
/// <item><description><c>CreatePage</c> 存在，并明确抛 <c>NotSupportedException</c>（不是返回 null）；</description></item>
/// <item><description>异常消息里带阶段标记，接手的人一眼知道这是待办不是缺陷；</description></item>
/// <item><description>页面工厂的接缝还在（<c>AppShell.PageFactory</c>），阶段 3 有地方接线；</description></item>
/// <item><description>三个页面将来要用的存储<b>仍然注册在组合根里</b>——
/// 这是原断言真正想守住的东西，页面没回来不代表它们可以被顺手删掉。</description></item>
/// </list>
/// 阶段 3/4 把页面迁回 WPF 后，请把本页恢复成逐页核对 <c>CreatePage</c> 构造分支的断言。
/// </para>
/// </remarks>
    [Fact]
    public void CompositionRootDefersPageAssemblyUntilPagesAreMigrated()
    {
        var source = RepositorySourceFile.Read(CompositionRootPath);
        var createPage = RepositorySourceFile.ExtractMethodBody(source, CreatePageSignature);

        Assert.False(
            string.IsNullOrEmpty(createPage),
            "AppCompositionRoot.cs 中找不到 CreatePage 方法体，页面工厂在阶段 3 之后无处接线。");

        Assert.True(
            Regex.IsMatch(createPage!, @"throw\s+new\s+NotSupportedException\s*\(", RegexOptions.CultureInvariant),
            "页面尚未迁移到 WPF 时，CreatePage 必须抛 NotSupportedException；" +
            "返回 null 或空壳会让阶段 3 拿到一个「看起来已完成」的组合根。");

        Assert.True(
            Regex.IsMatch(createPage!, @"阶段\s*3\s*/\s*4", RegexOptions.CultureInvariant),
            "CreatePage 的异常消息必须带上阶段标记，接手的人才会知道这是待办而不是缺陷。");

        // 页面工厂的接缝必须还在。
        var shell = RepositorySourceFile.Read("src/StreamingAggregation.App/Composition/AppShell.cs");
        Assert.True(
            Regex.IsMatch(shell, @"Func<Type,\s*Page>\s+PageFactory", RegexOptions.CultureInvariant),
            "AppShell.PageFactory 的签名不再是 Func<Type, Page>，阶段 3 接不上页面工厂。");

        // 三个页面将来要用的存储仍必须注册在组合根里（这是原断言真正守的东西）。
        // 注册写法是 AddSingleton<TService, TImplementation>（两个泛型实参），
        // 因此匹配 `<TStore,` 或 `<TStore>` 两种形式。
        foreach (var storeInterface in new[] { "IDigestStore", "ITaskStore", "IMessageStore" })
        {
            Assert.True(
                Regex.IsMatch(
                    source,
                    $@"AddSingleton<{storeInterface}\s*[,>]",
                    RegexOptions.CultureInvariant),
                $"组合根不再注册 {storeInterface}：页面迁回后没有存储可注入，只能显示空列表。");
        }
    }

    /// <summary>页面加载上下文必须真的走注入的封送委托，并把失败记进日志。</summary>
    [Fact]
    public void PageLoadContextRoutesUpdatesThroughTheInjectedDelegate()
    {
        var source = RepositorySourceFile.Read(PageLoadContextPath);

        var apply = RepositorySourceFile.ExtractMethodBody(source, "public void Apply(Action update)");
        Assert.False(
            string.IsNullOrEmpty(apply),
            "PageLoadContext.cs 中找不到 Apply 方法体。");
        Assert.True(
            Regex.IsMatch(apply!, @"_marshal\s*\(\s*update\s*\)", RegexOptions.CultureInvariant),
            "PageLoadContext.Apply 没有把更新交给注入的封送委托，异步加载又会退回非 UI 线程。");

        var warn = RepositorySourceFile.ExtractMethodBody(source, "public void Warn(string message)");
        Assert.False(
            string.IsNullOrEmpty(warn),
            "PageLoadContext.cs 中找不到 Warn 方法体。");
        Assert.True(
            Regex.IsMatch(warn!, @"Trace\.WriteLine", RegexOptions.CultureInvariant),
            "PageLoadContext.Warn 没有记录日志，页面加载失败将无声无息。");
    }

    /// <summary>取页面类型名：文件是 <c>XxxPage.xaml.cs</c>，类型名是 <c>XxxPage</c>。</summary>
    /// <param name="pagePath">页面代码后置文件相对仓库根的路径。</param>
    /// <returns>页面类型名。</returns>
    private static string PageName(string pagePath)
    {
        const string CodeBehindSuffix = ".xaml.cs";

        var fileName = Path.GetFileName(pagePath);
        return fileName.EndsWith(CodeBehindSuffix, StringComparison.Ordinal)
            ? fileName[..^CodeBehindSuffix.Length]
            : Path.GetFileNameWithoutExtension(fileName);
    }
}