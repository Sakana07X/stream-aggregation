using System.Text.RegularExpressions;
using StreamingAggregation.Tests.Fixtures;

namespace StreamingAggregation.Tests.Composition;

/// <summary>
/// 源码断言：摘要页、待办页、消息页必须真的读存储、走 UI 线程封送通道，而不是继续绑空集合。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是源码断言</b>：这三个页面是 WinUI 3 类型，测试宿主加载
/// <c>StreamingAggregation.App</c> 需要 XAML/WinRT 运行时（同
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
/// <item>忘了往组合根的 <c>CreatePage</c> 里注入存储，页面静默退化成空列表；
/// </item>
/// <item>待办页的完成/忽略按钮又变回空实现。
/// </item>
/// </list>
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

    /// <summary>组合根必须把存储和 UI 封送委托一起注入这三个页面。</summary>
    /// <remarks>
    /// 漏注入编译得过、页面也不报错，只会永远显示空列表——这类回归没有任何运行时信号。
    /// </remarks>
    [Fact]
    public void CompositionRootInjectsStoresAndTheUiMarshalDelegate()
    {
        var source = RepositorySourceFile.Read(CompositionRootPath);
        var createPage = RepositorySourceFile.ExtractMethodBody(source, CreatePageSignature);

        Assert.False(
            string.IsNullOrEmpty(createPage),
            "AppCompositionRoot.cs 中找不到 CreatePage 方法体，无法核对页面装配。");

        foreach (var (pageName, storeInterface) in new[]
                 {
                     ("DigestPage", "IDigestStore"),
                     ("TodoPage", "ITaskStore"),
                     ("MessagesPage", "IMessageStore"),
                 })
        {
            Assert.True(
                Regex.IsMatch(
                    createPage!,
                    $@"new\s+{pageName}\s*\(\s*_services\.Resolve<{storeInterface}>\s*\(\s*\)\s*,\s*QueueOnUiThread\s*\)",
                    RegexOptions.Singleline | RegexOptions.CultureInvariant),
                $"CreatePage 没有把 {storeInterface} 与 UI 线程封送委托注入 {pageName}。");
        }

        Assert.False(
            Regex.IsMatch(createPage!, @"new\s+(DigestPage|TodoPage|MessagesPage)\s*\(\s*\)", RegexOptions.CultureInvariant),
            "CreatePage 里还有无参构造的数据页，页面拿不到存储与封送委托。");
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