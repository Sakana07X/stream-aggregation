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
        var source = ReadSettingsViewModelSource();

        var validateBody = ExtractMethodBody(source, "public IReadOnlyList<string> Validate()");
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

    /// <summary>
    /// 读取 <c>SettingsViewModel.cs</c> 源码；找不到仓库根或文件时直接失败。
    /// </summary>
    /// <returns>源码全文。</returns>
    private static string ReadSettingsViewModelSource()
    {
        var root = RepositoryRootLocator.Find();
        Assert.True(
            root is not null,
            "未能从测试输出目录向上找到仓库根目录，无法核对设置页校验。");

        var sourcePath = Path.Combine(root!, SettingsViewModelRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(sourcePath), $"设置页视图模型源文件不存在：{SettingsViewModelRelativePath}");

        return File.ReadAllText(sourcePath);
    }

    /// <summary>
    /// 从源码中切出以指定签名起始的方法体（按大括号配对）。
    /// </summary>
    /// <remarks>
    /// 只用于本文件的快照断言：方法体内插值字符串（如 <c>$"... {MinPort} ..."</c>）
    /// 的大括号是配对的，不会干扰配对计数。
    /// </remarks>
    /// <param name="source">源码全文。</param>
    /// <param name="signature">方法签名（在文件中唯一）。</param>
    /// <returns>方法体（不含签名与最外层大括号）；找不到时返回 <see langword="null"/>。</returns>
    private static string? ExtractMethodBody(string source, string signature)
    {
        var signatureIndex = source.IndexOf(signature, StringComparison.Ordinal);
        if (signatureIndex < 0)
        {
            return null;
        }

        var openIndex = source.IndexOf('{', signatureIndex);
        if (openIndex < 0)
        {
            return null;
        }

        var depth = 0;
        for (var i = openIndex; i < source.Length; i++)
        {
            switch (source[i])
            {
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return source[(openIndex + 1)..i];
                    }

                    break;
            }
        }

        return null;
    }
}
