using System.Collections.Generic;
using System.Text;
using System.Xml.Linq;

namespace StreamingAggregation.App.Services;

/// <summary>
/// 通知呈现优先级。与 WinRT 的 <c>AppNotificationPriority</c> 解耦，
/// 使本文件不依赖任何 WinRT 类型、可脱离通知通道单独验证。
/// </summary>
public enum NotificationPriority
{
    /// <summary>默认权重。</summary>
    Normal,

    /// <summary>高权重，用于需要立即处理的事项。</summary>
    High,
}

/// <summary>
/// 一条待发送通知的完整载荷。
/// </summary>
/// <param name="Xml">toast XML，可直接交给 <c>new AppNotification(xml)</c>。</param>
/// <param name="Tag">通知标签；同一 tag 的新通知会替换旧通知。为 <see langword="null"/> 时不设置。</param>
/// <param name="Group">通知分组标签，用于在操作中心折叠展示。</param>
/// <param name="Priority">呈现优先级。</param>
public sealed record NotificationPayload(
    string Xml,
    string? Tag,
    string Group,
    NotificationPriority Priority);

/// <summary>
/// 通知模板：集中定义 toast 的 AUMID、分组标签、激活参数与 XML 骨架。
/// </summary>
/// <remarks>
/// 与 <see cref="ToastNotificationService"/> 的分工：本类只负责"长什么样"——纯数据加 XML 生成，
/// 不触碰任何 WinRT 类型；服务负责"能不能发、发不发"。这样模板可以脱离通知通道单独验证。
/// <para>
/// XML 一律用 <see cref="XElement"/> 构造而非字符串插值：消息正文来自不可控的群消息，
/// 其中的 <c>&lt;</c>、<c>&amp;</c> 会被自动转义；手写插值会产出非法 XML，
/// 整条通知将被系统静默丢弃。
/// </para>
/// <para>
/// 【阶段 2】本类的 XML 生成与截断逻辑已改成 net48 可编译（不再依赖
/// <c>string.EnumerateRunes</c> 与 <c>System.Text.Rune</c>），但它<b>尚未被真正使用</b>：
/// <c>ToastNotificationService</c> 已被降级为不发通知的桩（见该文件）。
/// 阶段 5 接上 <c>ToastNotificationManagerCompat</c> 时，本类的 payload 才重新上生产线。
/// </para>
/// </remarks>
public static class NotificationTemplates
{
    /// <summary>
    /// 本应用的通知标识（AUMID）。
    /// </summary>
    /// <remarks>
    /// unpackaged 应用没有包标识，而 Windows 通知平台要求每条通知有归属的 AUMID，
    /// 否则通知无处落地。本值作为后续统计、去重与开始菜单匹配的稳定键。
    /// <para>
    /// 【阶段 2】注册动作暂时没有执行者：<c>AppNotificationManager.Register()</c> 属于
    /// Windows App SDK，net48 上不存在。阶段 5 改用
    /// <c>ToastNotificationManagerCompat</c> 时，它会按 exe 路径自动写
    /// <c>HKCU\SOFTWARE\Classes\AppUserModelId\</c>（探针实测，无需管理员权限与
    /// 开始菜单快捷方式），届时本常量用于日志与去重键，而不是注册调用本身。
    /// </para>
    /// </remarks>
    public const string AppUserModelId = "StreamingAggregation.App.Notifications";

    /// <summary>激活参数键：通知类别，取值为 <see cref="UrgentAction"/> 或 <see cref="DeadlineAction"/>。</summary>
    public const string ActionArgumentKey = "action";

    /// <summary>激活参数值：紧急事项。</summary>
    public const string UrgentAction = "urgent";

    /// <summary>激活参数值：临近截止。</summary>
    public const string DeadlineAction = "deadline";

    /// <summary>激活参数键：来源标识（批次或行动项 ID）。</summary>
    public const string SourceIdArgumentKey = "sourceId";

    /// <summary>
    /// 通知分组标签。
    /// </summary>
    /// <remarks>
    /// 同组的通知会在操作中心折叠成一块，避免每条摘要都平铺一行把操作中心刷屏；
    /// 折叠不会丢通知，用户展开即可逐条查看。
    /// </remarks>
    public const string GroupTag = "streaming-aggregation";

    /// <summary>紧急通知的 tag 前缀；配合来源标识可实现"同一事项只保留最新一条"。</summary>
    public const string UrgentTagPrefix = "urgent:";

    /// <summary>临近截止通知的 tag 前缀。</summary>
    public const string DeadlineTagPrefix = "deadline:";

    /// <summary>toast 标题的最大字符数（ToastGeneric 模板只显示一行标题）。</summary>
    public const int MaxTitleLength = 40;

    /// <summary>toast 正文的最大字符数（ToastGeneric 模板默认显示两行正文）。</summary>
    public const int MaxMessageLength = 160;

    /// <summary>
    /// 构造"紧急事项"通知的载荷。
    /// </summary>
    /// <param name="title">标题，通常是紧急事项的一句话概括。</param>
    /// <param name="message">正文，通常是行动项描述。</param>
    /// <param name="sourceId">来源标识（批次/行动项 ID），用于定位与去重，可为空。</param>
    /// <param name="urgentScenario">
    /// 是否使用 <c>scenario="urgent"</c>。该场景仅部分 Windows 11 构建支持，
    /// 须由 <c>AppNotificationBuilder.IsUrgentScenarioSupported()</c> 探测后传入；
    /// 不支持时回落到默认场景，保证通知仍然能弹出。
    /// </param>
    /// <returns>通知载荷。</returns>
    public static NotificationPayload CreateUrgent(
        string title,
        string message,
        string? sourceId = null,
        bool urgentScenario = false)
    {
        var scenario = urgentScenario ? "urgent" : null;
        return new NotificationPayload(
            BuildToast(title, message, scenario, BuildLaunch(UrgentAction, sourceId)),
            BuildTag(UrgentTagPrefix, sourceId),
            GroupTag,
            NotificationPriority.High);
    }

    /// <summary>
    /// 构造"临近截止"通知的载荷。
    /// </summary>
    /// <remarks>
    /// 临近截止本身不等于紧急，因此固定使用默认场景、不申请 <c>urgent</c>——
    /// 避免把"还有 2 小时"和"服务器炸了"用同一种打断强度呈现。
    /// <para>
    /// <c>INotificationService.NotifyDeadline</c> 只传标题与正文，
    /// 因此剩余时间须由调用方写进正文，本方法不再自行拼接截止时间。
    /// </para>
    /// </remarks>
    /// <param name="title">标题。</param>
    /// <param name="message">正文（调用方应已包含剩余时间等关键信息）。</param>
    /// <param name="sourceId">来源标识（行动项 ID），可为空。</param>
    /// <returns>通知载荷。</returns>
    public static NotificationPayload CreateDeadline(string title, string message, string? sourceId = null)
    {
        return new NotificationPayload(
            BuildToast(title, message, scenario: null, BuildLaunch(DeadlineAction, sourceId)),
            BuildTag(DeadlineTagPrefix, sourceId),
            GroupTag,
            NotificationPriority.Normal);
    }

    /// <summary>
    /// 清理并截断标题。
    /// </summary>
    /// <remarks>
    /// 先剔除 XML 1.0 不允许的控制字符，再按 Unicode 标量（而非 UTF-16 码元）截断，
    /// 避免把代理对从中间劈开产生乱码。
    /// </remarks>
    /// <param name="title">原始标题。</param>
    /// <returns>可安全写入 XML 的标题文本。</returns>
    public static string NormalizeTitle(string? title) =>
        Truncate(Sanitize(title), MaxTitleLength);

    /// <summary>
    /// 清理并截断正文。
    /// </summary>
    /// <param name="message">原始正文。</param>
    /// <returns>可安全写入 XML 的正文文本。</returns>
    public static string NormalizeMessage(string? message) =>
        Truncate(Sanitize(message), MaxMessageLength);

    /// <summary>
    /// 拼接 toast 的 <c>launch</c> 激活参数字符串。
    /// </summary>
    /// <remarks>
    /// 约定为 <c>key=value</c>，多段以 <c>&amp;</c> 连接；点击通知后由
    /// <c>NotificationInvoked</c> 原样取回解析。首版只携带类别与来源标识。
    /// </remarks>
    /// <param name="action">通知类别值。</param>
    /// <param name="sourceId">来源标识，可为空。</param>
    /// <returns><c>launch</c> 属性值。</returns>
    public static string BuildLaunch(string action, string? sourceId)
    {
        var launch = $"{ActionArgumentKey}={action}";
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            return launch;
        }

        return $"{launch}&{SourceIdArgumentKey}={Uri.EscapeDataString(Sanitize(sourceId))}";
    }

    // 【net48】string.IsNullOrWhiteSpace 在 .NET Framework 上没有 [NotNullWhen(false)] 标注
    // （.NET Core 3.0 起才有），因此后面的解引用必须自己写 !，否则 CS8602 逐处报警。
    private static string? BuildTag(string prefix, string? sourceId) =>
        string.IsNullOrWhiteSpace(sourceId) ? null : prefix + sourceId!.Trim();

    private static string BuildToast(
        string? title,
        string? message,
        string? scenario,
        string launch)
    {
        var toast = new XElement(
            "toast",
            // launch 决定点击通知后如何回到应用，Windows 会原样回传给 NotificationInvoked。
            new XAttribute("launch", launch),
            // scenario="urgent" 仅在受支持的 Windows 11 构建上有效，其余情况必须整个省略该属性。
            scenario is null ? null : new XAttribute("scenario", scenario),
            new XElement(
                "visual",
                new XElement(
                    "binding",
                    new XAttribute("template", "ToastGeneric"),
                    new XElement("text", NormalizeTitle(title)),
                    new XElement("text", NormalizeMessage(message)))));

        return toast.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// 剔除 XML 1.0 不允许的字符，并把换行与连续空白压成单个空格。
    /// </summary>
    /// <remarks>
    /// <see cref="XElement"/> 遇到非法字符会直接抛异常，而正文来自不可控的群消息，必须先净化。
    /// 制表/换行/回车在 XML 里合法，但 toast 只渲染有限行数，压成空格比交给系统换行更可预测。
    /// </remarks>
    private static string Sanitize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text!.Length);

        // 【net48】string.EnumerateRunes / System.Text.Rune 都是 .NET Core 2.1+ 的 API，
        // net48 上不存在，且 PolySharp 补不了（源生成器只能造类型，不能给既有类型加方法）。
        // 这里改为自行按 UTF-16 代理对解码出 Unicode 标量值，语义与 EnumerateRunes 相同：
        // 合法代理对合成一个大于 U+FFFF 的标量，孤立代理项原样作为标量返回（随后会被
        // IsXmlChar 过滤掉，因为代理区码位不属于 XML 1.0 允许的字符集）。
        foreach (var scalar in EnumerateScalars(text))
        {
            if (scalar is '\t' or '\n' or '\r')
            {
                builder.Append(' ');
                continue;
            }

            if (IsXmlChar(scalar))
            {
                AppendScalar(builder, scalar);
            }
        }

        return CollapseWhitespace(builder.ToString());
    }

    /// <summary>
    /// 按 Unicode 标量（而非 UTF-16 码元）遍历字符串。
    /// </summary>
    /// <remarks>
    /// 逐项返回 <see cref="int"/> 标量值而不是字符串：<c>System.Text.Rune</c> 这个类型在 net48 上不存在，
    /// 用 <see cref="int"/> 承载标量值是唯一不需要新包的做法。
    /// </remarks>
    /// <param name="text">待遍历的文本。</param>
    /// <returns>标量值序列。</returns>
    private static IEnumerable<int> EnumerateScalars(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            char current = text[index];

            // 高代理字符只有在紧跟低代理字符时才组成一个标量；其余情况一律当作独立码元。
            if (char.IsHighSurrogate(current)
                && index + 1 < text.Length
                && char.IsLowSurrogate(text[index + 1]))
            {
                yield return char.ConvertToUtf32(current, text[index + 1]);
                index++;
                continue;
            }

            yield return current;
        }
    }

    /// <summary>
    /// 把一个 Unicode 标量按 UTF-16 追加到 <see cref="StringBuilder"/>。
    /// </summary>
    /// <remarks>
    /// 代理区码位（U+D800–U+DFFF）不是合法标量，<see cref="char.ConvertFromUtf32"/> 会对它们抛异常；
    /// 调用方必须先经 <see cref="IsXmlChar"/> 过滤，这里再补一道防御，避免误用直接崩掉通知生成。
    /// </remarks>
    /// <param name="builder">目标。</param>
    /// <param name="scalar">Unicode 标量值。</param>
    private static void AppendScalar(StringBuilder builder, int scalar)
    {
        if (scalar is >= 0xD800 and <= 0xDFFF)
        {
            return;
        }

        builder.Append(char.ConvertFromUtf32(scalar));
    }

    /// <summary>判断 Unicode 标量值是否属于 XML 1.0 允许的字符集。</summary>
    private static bool IsXmlChar(int value) => value switch
    {
        0x9 or 0xA or 0xD => true,
        >= 0x20 and <= 0xD7FF => true,
        >= 0xE000 and <= 0xFFFD => true,
        >= 0x10000 and <= 0x10FFFF => true,
        _ => false,
    };

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// 按 Unicode 标量（而非 UTF-16 码元）截断，避免把代理对从中间劈开产生乱码。
    /// </summary>
    /// <remarks>
    /// 省略号计入上限，最终长度不会超过 <paramref name="maxLength"/>——
    /// 否则 <see cref="MaxTitleLength"/> 之类的常量名不副实。
    /// </remarks>
    private static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        // 两个上限都远大于 1，这里的防御分支只为避免极端配置下的 Take(-1)。
        var keep = Math.Max(0, maxLength - 1);

        // 必须按 Unicode 标量逐个转成 UTF-16 再拼接：按码元截断会把代理对从中间劈开产生乱码。
        var builder = new StringBuilder(keep + 1);
        var taken = 0;

        foreach (var scalar in EnumerateScalars(text))
        {
            if (taken == keep)
            {
                break;
            }

            AppendScalar(builder, scalar);
            taken++;
        }

        builder.Append('…');
        return builder.ToString();
    }
}