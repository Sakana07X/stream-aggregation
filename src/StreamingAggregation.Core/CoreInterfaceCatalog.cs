using System.Reflection;

namespace StreamingAggregation.Core;

/// <summary>
/// Core 公开接口目录：反射出"这个库对外承诺了哪些接口"。
/// </summary>
/// <remarks>
/// <para>
/// 这个目录被两处共用，所以放在 Core 而不是调用方：
/// </para>
/// <list type="bullet">
///   <item><description>
///     App 的组合根在启动时据此自检——<b>新增接口却忘了登记实现</b>会在启动阶段立刻失败，
///     而不是等到某条消息进来才空引用。
///   </description></item>
///   <item><description>
///     测试据此逐个断言"每个接口都有实现"。
///   </description></item>
/// </list>
/// <para>
/// 结果做了缓存：接口集合在程序集加载后就不会再变，没必要每次自检都重新反射。
/// </para>
/// </remarks>
public static class CoreInterfaceCatalog
{
    private static readonly Lazy<IReadOnlyList<Type>> Cache = new(Discover, isThreadSafe: true);

    /// <summary>
    /// 枚举 Core 中声明的全部公开接口，按全名排序，保证顺序稳定可比对。
    /// </summary>
    /// <remarks>
    /// 只取"本程序集声明"的接口（<c>DeclaredOnly</c>），不含继承而来的成员，
    /// 也不含泛型接口与非接口嵌套类型——它们在当前契约里都没有对应实现。
    /// </remarks>
    /// <returns>接口类型列表。</returns>
    public static IReadOnlyList<Type> Enumerate() => Cache.Value;

    /// <summary>执行反射发现。</summary>
    /// <returns>接口类型列表。</returns>
    private static IReadOnlyList<Type> Discover()
    {
        var assembly = typeof(CoreInterfaceCatalog).Assembly;

        return
        [
            .. assembly
                .GetExportedTypes()
                .Where(static type => type is { IsInterface: true, IsGenericTypeDefinition: false, DeclaringType: null })
                .OrderBy(static type => type.FullName, StringComparer.Ordinal),
        ];
    }
}