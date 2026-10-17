using System.Text.RegularExpressions;
using StreamingAggregation.Tests.Fixtures;

namespace StreamingAggregation.Tests.Composition;

/// <summary>
/// 源码断言：托盘「暂停处理」菜单项必须绑定 Command 接到暂停/恢复服务，
/// 且菜单文案要随暂停状态在「暂停处理」与「恢复处理」之间切换。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不直接 new TrayIconService 测行为</b>：<c>StreamingAggregation.App</c> 是
/// WPF + net48 可执行程序集，测试宿主是一个控制台进程，加载它需要 STA 线程与 WPF
/// 消息泵（同 <see cref="SettingsPageSecurityTests"/> 的说明），因此改为核对源码。
/// </para>
/// <para>
/// 这条测试挡住的是两类回归：暂停项的 <c>Command</c> 被删掉（托盘菜单重新点了没反应），
/// 以及菜单文案不再随状态更新（暂停后仍显示「暂停处理」）。
/// </para>
/// <para>
/// <b>阶段 2 迁移</b>：WinUI 的 <c>MenuFlyout</c>/<c>MenuFlyoutItem</c> 换成 WPF 的
/// <c>ContextMenu</c>/<c>MenuItem</c>，因此下面锚定的方法签名也跟着换；
/// 「必须绑定 Command、必须保留 Tag」这两条断言的意图不变。
/// </para>
/// </remarks>
public sealed class TrayPauseMenuTests
{
    /// <summary><c>TrayIconService.cs</c> 相对仓库根的路径。</summary>
    private const string TrayIconServiceRelativePath =
        "src/StreamingAggregation.App/Services/TrayIconService.cs";

    /// <summary><c>AppCompositionRoot.cs</c> 相对仓库根的路径。</summary>
    private const string AppCompositionRootRelativePath =
        "src/StreamingAggregation.App/Composition/AppCompositionRoot.cs";

    /// <summary>
    /// 「暂停处理」菜单项必须绑定 <c>Command</c>（接到暂停/恢复动作），
    /// 而不是只设一个 <c>Tag</c>——后者正是本 bug 的成因。
    /// </summary>
    [Fact]
    public void PauseMenuItemHasCommandBinding()
    {
        var source = RepositorySourceFile.Read(TrayIconServiceRelativePath);

        var buildMenuBody = RepositorySourceFile.ExtractMethodBody(source, "private ContextMenu BuildContextMenu()");
        Assert.False(
            string.IsNullOrEmpty(buildMenuBody),
            "TrayIconService.cs 中找不到 BuildContextMenu() 方法体，无法核对暂停项绑定。");

        // 暂停项的初始化块里必须出现 Command = new RelayCommand(_togglePauseAction)
        Assert.True(
            Regex.IsMatch(
                buildMenuBody!,
                @"new\s+RelayCommand\(\s*_togglePauseAction\s*\)"),
            "暂停处理菜单项未绑定 Command（只设 Tag），点击不会有任何反应。");

        // 暂停项仍保留 Tag，供测试/自动化定位
        Assert.Contains("PauseMenuItem", buildMenuBody);
    }

    /// <summary>
    /// 菜单文案必须随暂停状态在「暂停处理」与「恢复处理」之间切换。
    /// </summary>
    [Fact]
    public void PauseMenuTextFollowsPauseState()
    {
        var source = RepositorySourceFile.Read(TrayIconServiceRelativePath);

        var updateBody = RepositorySourceFile.ExtractMethodBody(source, "public void UpdatePauseState()");
        Assert.False(
            string.IsNullOrEmpty(updateBody),
            "TrayIconService.cs 中找不到 UpdatePauseState() 方法体，无法核对菜单文案更新逻辑。");

        // 两种文案都必须存在
        Assert.Contains("暂停处理", updateBody);
        Assert.Contains("恢复处理", updateBody);

        // 文案必须按当前暂停状态选择，而不是写死其中一种
        Assert.Contains("_isPausedProvider", updateBody);
    }

    /// <summary>
    /// 组合根必须把暂停/恢复动作传给托盘，并订阅状态变化以刷新菜单文案。
    /// </summary>
    [Fact]
    public void CompositionRootWiresPauseActionAndRefresh()
    {
        var source = RepositorySourceFile.Read(AppCompositionRootRelativePath);

        // 构造托盘时传入 togglePauseAction，接到 PauseResume.Toggle
        Assert.Contains("togglePauseAction: PauseResume.Toggle", source);

        // 暂停状态变化后同步托盘菜单文案
        Assert.Contains("PauseResume.PauseStateChanged +=", source);
        Assert.Contains("_tray?.UpdatePauseState()", source);
    }
}
