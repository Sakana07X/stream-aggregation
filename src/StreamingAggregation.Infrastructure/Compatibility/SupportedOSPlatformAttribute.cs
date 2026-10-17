// System.Runtime.Versioning.SupportedOSPlatformAttribute —— net48 兼容垫片。
//
// [SupportedOSPlatform("windows")] 是 .NET 5 引入的 BCL 特性，net48 的 mscorlib
// 没有它。问题在于 PolySharp 不生成它（实测）：PolySharp 走的是
// PolySharp.SourceGenerators 的类型生成器，只补"整个类型都不存在"的语言级特性
// （IsExternalInit、RequiredMemberAttribute、Index/Range…），
// SupportedOSPlatformAttribute 连同它的父类/枚举语义都要按 BCL 行为复刻，
// 不在它的生成清单里。
//
// 更麻烦的是"不声明会怎样"：Roslyn 在编译单元缺少该特性时会自己合成一个
// internal 版本，于是用户代码写 [SupportedOSPlatform("windows")] 会拿到
// CS0122「因它具有更高的保护级别而不可访问」。也就是说，缺着不写反而一定编译不过。
//
// 声明为 internal 的理由：
//   - 不进 Infrastructure 的 public API，也不扩大程序集边界；
//   - net48 上没有 CA1416 分析器消费它，它在这里只承担"编译期声明"的职责；
//   - Tests / App 若也需要，自行在各自程序集里声明一份（与本仓库
//     Compatibility/SystemThreadingLock.cs 的"三个项目各一份、互不依赖"做法一致）。

namespace System.Runtime.Versioning;

/// <summary>
/// 标记 API、程序集或类型只在指定的平台上可用（.NET 5 引入，net48 上自行声明）。
/// </summary>
[AttributeUsage(
    AttributeTargets.Assembly
    | AttributeTargets.Class
    | AttributeTargets.Constructor
    | AttributeTargets.Enum
    | AttributeTargets.Event
    | AttributeTargets.Field
    | AttributeTargets.Interface
    | AttributeTargets.Method
    | AttributeTargets.Module
    | AttributeTargets.Property
    | AttributeTargets.Struct,
    AllowMultiple = false,
    Inherited = false)]
internal sealed class SupportedOSPlatformAttribute : Attribute
{
    /// <summary>创建特性实例。</summary>
    /// <param name="platformName">平台名称，例如 <c>windows</c>。</param>
    public SupportedOSPlatformAttribute(string platformName) => PlatformName = platformName;

    /// <summary>受支持的平台名称。</summary>
    public string PlatformName { get; }

    /// <summary>不受支持时抛出的异常类型名。</summary>
    public string? Message { get; set; }
}
