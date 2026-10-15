using System.Text.RegularExpressions;
using StreamingAggregation.Tests.Fixtures;

namespace StreamingAggregation.Tests.Composition;

/// <summary>
/// 源码断言：设置页校验必须执行传输策略（远程 OneBot 连接强制 WSS、远程模型端点强制 HTTPS）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不直接 new SettingsViewModel 测行为</b>：<c>StreamingAggregation.App</c> 是
/// WinUI unpackaged 程序集，测试宿主加载它需要 XAML/WinRT 运行时（同
/// <see cref="CoreInterfaceCoverageTests"/> 的说明），因此改为核对
/// <c>SettingsViewModel.cs</c> 源码：<see cref="SettingsViewModel"/> 的 <c>Validate()</c>
/// 必须调用 <c>EndpointTransportPolicy</c>，并包含 WSS / HTTPS 两条错误描述。
/// </para>
/// <para>
/// 这条测试挡住的是"校验规则被删掉或改错文案"这类回归；规则本身的回环判定逻辑由
/// Core 层的 <c>EndpointTransportPolicy</c> 及其测试负责。
/// </para>
/// </remarks>
public sealed class SettingsPageSecurityTests
{
    /// <summary><c>SettingsViewModel.cs</c> 相对仓库根的路径。</summary>
    private const string SettingsViewModelRelativePath =
        "src/StreamingAggregation.App/ViewModels/SettingsViewModel.cs";

    /// <summary>
    /// <c>Validate()</c> 必须复用 <c>EndpointTransportPolicy</c> 做回环判定，
    /// 并对远程 OneBot 连接与远程模型端点分别强制 WSS / HTTPS。
    /// </summary>
    [Fact]
    public void ValidateEnforcesTransportPolicyForRemoteEndpoints()
    {
        var source = RepositorySourceFile.Read(SettingsViewModelRelativePath);

        var validateBody = RepositorySourceFile.ExtractMethodBody(source, "public IReadOnlyList<string> Validate()");
        Assert.False(
            string.IsNullOrEmpty(validateBody),
            "SettingsViewModel.cs 中找不到 Validate() 方法体，无法核对传输策略校验。");

        // Validate() 必须复用 EndpointTransportPolicy 做回环判定（OneBot 主机与模型端点各一次调用）
        Assert.True(
            Regex.IsMatch(validateBody!, @"EndpointTransportPolicy\.IsLoopback\s*\("),
            "Validate() 未调用 EndpointTransportPolicy.IsLoopback，远程连接传输策略校验缺失。");

        // 远程 OneBot 连接必须使用 WSS
        Assert.True(
            Regex.IsMatch(validateBody!, @"远程连接必须使用\s*WSS"),
            "Validate() 缺少“远程连接必须使用 WSS”错误描述。");

        // 远程模型端点必须使用 HTTPS
        Assert.True(
            Regex.IsMatch(validateBody!, @"远程端点必须使用\s*HTTPS"),
            "Validate() 缺少“远程端点必须使用 HTTPS”错误描述。");
    }
}
