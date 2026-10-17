using System.Reflection;
using System.Text.RegularExpressions;
using StreamingAggregation.Core;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Infrastructure.Persistence;
using StreamingAggregation.Tests.Fixtures;

namespace StreamingAggregation.Tests.Composition;

/// <summary>
/// 反射测试：Core 对外声明的每个接口都必须有可用实现。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要这条测试</b>：Core 只定义接口，具体实现分散在 Infrastructure 与 App 两个程序集里。
/// 新加一个接口时，谁都不会顺手把实现补上——编译依然通过，缺的那行代码要到运行时才炸。
/// 这条测试把"接口有实现"变成一条会红的断言。
/// </para>
/// <para>
/// <b>为什么不全靠反射找实现</b>：<c>StreamingAggregation.App</c> 是 WinUI unpackaged 程序集，
/// 测试宿主加载它需要 XAML/WinRT 运行时，而且类型加载在无消息循环的测试进程里不可靠。
/// 因此对"只有 App 层提供实现"的接口，改用 <see cref="AppLayerProviders"/> 显式声明实现类型，
/// 并在测试里核对源文件确实声明了该类型——同样能挡住"接口加了但实现没写"的回归，
/// 又不必把整个 WinUI 栈拖进测试宿主。
/// </para>
/// <para>
/// App 侧的组合根在启动时还会用 <see cref="CoreInterfaceCatalog"/> 做一次同样的自检，
/// 两处检查互为补充：测试挡提交，启动自检挡漏网。
/// </para>
/// </remarks>
public sealed class CoreInterfaceCoverageTests
{
    /// <summary>参与"找实现"的程序集：Core 自身与 Infrastructure。</summary>
    private static readonly Assembly[] ProviderAssemblies =
    [
        typeof(CoreInterfaceCatalog).Assembly,
        typeof(SqliteConnectionFactory).Assembly,
    ];

    /// <summary>
    /// 只在 App 层提供实现的接口 → 实现类型声明位置。
    /// </summary>
    /// <remarks>
    /// 这里写的是类型全名与源文件相对路径，不是 <see cref="Type"/>：App 程序集在测试宿主里
    /// 不可加载，只能按名字与源码核对。新增此类实现时在此登记一行。
    /// </remarks>
    private static readonly IReadOnlyDictionary<Type, (string Implementation, string SourceRelativePath)> AppLayerProviders
        = new Dictionary<Type, (string, string)>
        {
            // 通知通道必须跑在桌面线程模型上（Windows 通知平台），因此实现放在 App 而非 Infrastructure。
            [typeof(INotificationService)] =
                ("StreamingAggregation.App.Services.ToastNotificationService", "src/StreamingAggregation.App/Services/ToastNotificationService.cs"),
        };

    /// <summary>
    /// 为 Core 的每个公开接口生成一条测试数据。
    /// </summary>
    /// <returns>接口全名列表。</returns>
    public static TheoryData<string> CoreInterfaceNames()
    {
        var data = new TheoryData<string>();

        foreach (var service in CoreInterfaceCatalog.Enumerate())
        {
            data.Add(service.FullName ?? service.Name);
        }

        return data;
    }

    /// <summary>
    /// 每个 Core 接口都至少有一个具体实现。
    /// </summary>
    /// <param name="interfaceName">接口全名。</param>
    [Theory]
    [MemberData(nameof(CoreInterfaceNames))]
    public void EveryCoreInterfaceHasAnImplementation(string interfaceName)
    {
        var service = CoreInterfaceCatalog.Enumerate()
            .Single(type => string.Equals(type.FullName, interfaceName, StringComparison.Ordinal));

        var implementations = FindImplementationsInAssemblies(service);

        if (implementations.Count > 0)
        {
            Assert.NotEmpty(implementations);
            return;
        }

        if (AppLayerProviders.TryGetValue(service, out var appProvider))
        {
            AssertAppLayerProvider(service, appProvider.Implementation, appProvider.SourceRelativePath);
            return;
        }

        Assert.Fail(
            $"Core 接口 {interfaceName} 没有任何实现。" +
            "请在 Infrastructure 中提供实现，或在 CoreInterfaceCoverageTests.AppLayerProviders 中登记 App 层实现。");
    }

    /// <summary>
    /// App 层实现登记项本身必须指向 Core 里真实存在的接口，且实现声明仍然在源码里。
    /// </summary>
    [Fact]
    public void AppLayerProvidersEntriesStillPointAtDeclaredImplementations()
    {
        Assert.NotEmpty(AppLayerProviders);

        var coreInterfaces = CoreInterfaceCatalog.Enumerate().ToHashSet();

        // net48 的 KeyValuePair<K, V> 没有 Deconstruct 方法（netstandard2.1 才有），
        // 改成显式取 Key / Value，断言语义不变。
        foreach (var entry in AppLayerProviders)
        {
            Assert.Contains(entry.Key, coreInterfaces);
            AssertAppLayerProvider(entry.Key, entry.Value.Implementation, entry.Value.SourceRelativePath);
        }
    }

    /// <summary>
    /// 在 <see cref="ProviderAssemblies"/> 里找接口的具体实现。
    /// </summary>
    /// <param name="service">服务接口。</param>
    /// <returns>实现类型列表（可能为空）。</returns>
    private static IReadOnlyList<Type> FindImplementationsInAssemblies(Type service) =>
    [
        .. ProviderAssemblies
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => service.IsAssignableFrom(type))
            .Where(type => type is { IsInterface: false, IsAbstract: false, IsGenericTypeDefinition: false })
            .OrderBy(type => type.FullName, StringComparer.Ordinal),
    ];

    /// <summary>
    /// 核对 App 层实现：源文件存在、且确实声明了"实现该接口"的类。
    /// </summary>
    /// <param name="service">服务接口。</param>
    /// <param name="implementation">实现类型全名。</param>
    /// <param name="sourceRelativePath">实现源文件相对仓库根的路径。</param>
    private static void AssertAppLayerProvider(Type service, string implementation, string sourceRelativePath)
    {
        var root = RepositoryRootLocator.Find();
        Assert.True(
            root is not null,
            $"未能从测试输出目录向上找到仓库根目录，无法核对 App 层实现。");

        var sourcePath = Path.Combine(root!, sourceRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(sourcePath), $"App 层实现源文件不存在：{sourceRelativePath}");

        var source = File.ReadAllText(sourcePath);
        var typeName = implementation[(implementation.LastIndexOf('.') + 1)..];

        var declaresImplementation = Regex.IsMatch(
            source,
            $@"class\s+{Regex.Escape(typeName)}\b[^{{]*:\s*[^{{]*\b{Regex.Escape(service.Name)}\b",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);

        Assert.True(
            declaresImplementation,
            $"{sourceRelativePath} 未声明“{typeName} : {service.Name}”，接口 {service.FullName} 实际没有实现。");
    }


}