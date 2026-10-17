using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace StreamingAggregation.App.Services;

/// <summary>
/// 启动早期致命错误的兜底对话框：直接调 Win32 <c>MessageBoxW</c>，不经过 WPF 界面对象。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不用 WPF 的窗口内对话框</b>：走到这条路径的原因正是"主窗口还没建起来"，
/// 而任何 WPF 对话框都必须先有一个能承载它的窗口、还要在 UI 线程上等消息泵空下来——
/// 恰恰是此刻没有的东西。用界面框架去报"界面还不可用"的错，本身就是死结。
/// <c>MessageBoxW</c> 只依赖线程消息泵，不依赖任何 WPF 对象图，因此在任何阶段都还活着。
/// </para>
/// <para>
/// <b>脱敏取舍</b>：对话框文本会被用户截图、进 Issue、进工单，因此显示前统一清洗——
/// 抹掉 URL 里的 user:password、<c>key=value</c> 形态的密钥、鉴权头、常见 API Key 前缀
/// 与长不透明令牌；<b>但保留文件绝对路径</b>。"哪个 db 不可写、哪个目录没有权限"
/// 正是用户唯一需要的信息，脱敏掉它等于让这个兜底框变得没用。
/// </para>
/// <para>
/// <b>永不抛出</b>：user32 缺失、入口点不存在——一切失败都退化成
/// <see cref="Trace"/>。调用方的"要不要退出进程"决策不能因为"错误框自己出错"而丢失，
/// 那会把一次可见的失败重新变成静默失败。
/// </para>
/// </remarks>
internal static class FatalErrorDialog
{
    /// <summary>兜底对话框标题；窗口尚未建立，没有"窗口内对话框"可共用同一份文案。</summary>
    public const string DefaultTitle = "流式聚合启动失败";

    /// <summary>脱敏后仍为空时使用的占位文案。</summary>
    private const string UnknownFailureText = "（未能取得错误详情）";

    /// <summary>掩码标记。脱敏只替换、不删除，保留"这里原本有一段内容"的语义。</summary>
    private const string Mask = "***";

    /// <summary>文本上限。MessageBox 显示不了这么多，再多也只会挤成一团。</summary>
    private const int MaxTextLength = 1000;

    /// <summary>截断标记。</summary>
    private const string TruncationMarker = "…（内容过长，已截断）";

    /// <summary>内层异常展开的最大层数，防止自引用异常链把文本撑爆。</summary>
    private const int MaxExceptionDepth = 5;

    private const uint MbOk = 0x0000_0000;
    private const uint MbError = 0x0000_0010;

    /// <summary>
    /// 置顶。失败发生在工作线程上，若不置顶，对话框很容易被别的窗口盖住——
    /// 那等于又回到"用户什么都没看见"。
    /// </summary>
    private const uint MbTopMost = 0x0004_0000;

    /// <summary>URL 里的 user:password 部分。userinfo 段按 RFC 不含 <c>/</c>，用它收敛匹配边界。</summary>
    private static readonly Regex UrlUserInfoPattern = new(
        @"(?<scheme>[A-Za-z][A-Za-z0-9+.\-]*://)[^\s""'/@]+@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// <c>key=value</c> / <c>key: value</c> 形态的密钥赋值（ASCII 与全角分隔符都认）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 键名清单刻意不含裸 <c>key</c>：本项目到处是 <c>…SecretKey</c>、<c>…TokenSecretKey</c>
    /// 这类"密钥槽位名"，把它们连同 <c>=</c> 一起打码会毁掉诊断信息。
    /// 键名只认 ASCII：中文句子里的"密钥/令牌"不会被误当成赋值键。
    /// </para>
    /// <para>
    /// 值一侧<b>不能</b>排除 ASCII 的 <c>,</c> <c>;</c>：密码本来就可能含逗号，
    /// 少抹一截就是漏密钥。只排除中文标点——它们在密钥里不会出现，却常常紧跟在句子中间。
    /// </para>
    /// </remarks>
    private static readonly Regex SensitiveAssignmentPattern = new(
        @"(?<key>[A-Za-z0-9_\-]*(?:password|passwd|pwd|token|secret|api[_-]?key|access[_-]?key|access[_-]?token|credential|authorization)[A-Za-z0-9_\-]*)\s*[=＝:：]\s*(?!(?:Bearer|Basic)\b)[^\s，。；、：！？【】「」『』]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>HTTP 鉴权头：<c>Bearer &lt;token&gt;</c> / <c>Basic &lt;base64&gt;</c>。</summary>
    private static readonly Regex AuthSchemePattern = new(
        @"(?<scheme>\bBearer|\bBasic)\s+[A-Za-z0-9\-._~+/=]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>已知 API Key 前缀（OpenAI/Anthropic/Stripe 风格、GitHub、AWS）。</summary>
    private static readonly Regex KnownApiKeyPattern = new(
        @"\b(?:sk|pk|rk)[-_][A-Za-z0-9_\-]{8,}|\bgh[pousr]_[A-Za-z0-9]{16,}|\bAKIA[0-9A-Z]{16}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 兜底：长度 ≥ 40 的不透明令牌（JWT、DPAPI 密文、十六进制串）。
    /// </summary>
    /// <remarks>
    /// 左右边界都排除 <c>\</c>、<c>/</c>、<c>:</c>，因此路径里的长目录名
    /// （如某些应用的包目录）不会被当成令牌抹掉——路径是要保留的诊断信息。
    /// </remarks>
    private static readonly Regex LongOpaqueTokenPattern = new(
        @"(?<![A-Za-z0-9_\-:/\\])[A-Za-z0-9+/=_\-]{40,}(?![A-Za-z0-9_\-:/\\])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 显示致命错误对话框。永不抛出；任何失败都只写 <see cref="Trace"/>。
    /// </summary>
    /// <param name="title">标题；为空时用 <see cref="DefaultTitle"/>。</param>
    /// <param name="message">正文；显示前会经过 <see cref="Sanitize"/> 与长度截断。</param>
    public static void Show(string title, string message)
    {
        var text = Truncate(Sanitize(message));
        var caption = string.IsNullOrWhiteSpace(title) ? DefaultTitle : Truncate(title);

        // 调试输出会进日志文件，因此同样先脱敏——本地日志不该成为密钥的二次泄漏面。
        Trace.WriteLine($"[FatalError] {caption}: {text}");

        // net48 没有 OperatingSystem.IsWindows（.NET 5 起）；RuntimeInformation 在
        // .NET Framework 4.7.1 起就是 mscorlib 里的内建类型，直接用即可。
        // 判据保持"进程能否加载 user32"这一层含义，而不是简单的 OS 版本号。
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Trace.WriteLine("[FatalError] 非 Windows 平台，没有 Win32 兜底对话框可用。");
            return;
        }

        try
        {
            MessageBoxW(IntPtr.Zero, text, caption, MbOk | MbError | MbTopMost);
        }
        catch (Exception ex)
        {
            // user32 缺失、入口点不存在、调用被拒绝……兜底自己再失败就只剩日志。
            Trace.WriteLine($"[FatalError] 兜底对话框无法显示：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 把异常链压成一行行可读文本，并逐条脱敏。
    /// </summary>
    /// <param name="error">异常；为 <see langword="null"/> 时返回占位文案。</param>
    /// <returns>已脱敏、已截断的描述文本。</returns>
    public static string Describe(Exception? error)
    {
        if (error is null)
        {
            return UnknownFailureText;
        }

        var builder = new StringBuilder();
        var current = error;

        for (var depth = 0; current is not null && depth < MaxExceptionDepth; depth++)
        {
            if (depth > 0)
            {
                builder.Append(Environment.NewLine).Append("  内层异常：");
            }

            // 只取类型名与消息：堆栈对用户没有意义，而它的长度会把提示框撑爆。
            builder.Append(current.GetType().Name);

            var message = Sanitize(current.Message);
            if (!string.IsNullOrEmpty(message))
            {
                builder.Append(": ").Append(message);
            }

            current = current.InnerException;
        }

        return Truncate(builder.ToString());
    }

    /// <summary>
    /// 清洗文本里的密钥痕迹。
    /// </summary>
    /// <remarks>
    /// 逐条替换，命中哪条打哪条，<b>不整体清空</b>——文件路径、端点、异常类型名都要留着。
    /// 顺序也有讲究：先剥 <c>Authorization: Bearer &lt;token&gt;</c> 这种"键 + 鉴权方案"，
    /// 再按赋值上下文打码，最后才用长令牌规则兜底。
    /// 若让赋值规则先跑，它会把 <c>Authorization</c> 的值识别成 <c>Bearer</c>，
    /// 真正的令牌反而漏在后面。
    /// </remarks>
    /// <param name="message">原始文本。</param>
    /// <returns>清洗后的文本；输入为 <see langword="null"/> 或空白时返回空串。</returns>
    public static string Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var cleaned = UrlUserInfoPattern.Replace(message, $@"${{scheme}}{Mask}@");
        cleaned = AuthSchemePattern.Replace(cleaned, $"${{scheme}} {Mask}");
        cleaned = SensitiveAssignmentPattern.Replace(cleaned, $"${{key}}={Mask}");
        cleaned = KnownApiKeyPattern.Replace(cleaned, Mask);
        cleaned = LongOpaqueTokenPattern.Replace(cleaned, Mask);

        return cleaned;
    }

    /// <summary>截断过长文本，并避免把代理对从中间劈开。</summary>
    /// <param name="text">文本。</param>
    /// <returns>长度受控的文本。</returns>
    private static string Truncate(string text)
    {
        if (text.Length <= MaxTextLength)
        {
            return text;
        }

        var cut = MaxTextLength - TruncationMarker.Length;

        // 末尾若是高代理字符，再退一个，否则会留下半个代理对。
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        // string.Concat(ReadOnlySpan<char>, ReadOnlySpan<char>) 是 .NET Core 的重载，net48 上没有；
        // 必须走 Substring，编译期就会把这件事暴露出来，而不是运行时才 MissingMethod。
        return text.Substring(0, cut) + TruncationMarker;
    }

    /// <summary>
    /// Win32 <c>MessageBoxW</c>。
    /// </summary>
    /// <remarks>
    /// <c>ExactSpelling = true</c> 不可省：<c>CharSet.Unicode</c> 下默认的自动后缀探测
    /// 会把名字再拼成 <c>MessageBoxWW</c> 去 user32 里找，找不到才回退到本名。
    /// 显式关掉后缀探测，才能直接命中真正的导出名。
    /// 不设 <c>SetLastError</c>：这里不区分调用失败的具体原因，末次错误码没人读。
    /// </remarks>
    /// <param name="hWnd">父窗口句柄；无需归属到任何窗口时传 <see cref="IntPtr.Zero"/>。</param>
    /// <param name="lpText">正文。</param>
    /// <param name="lpCaption">标题。</param>
    /// <param name="uType">按钮与行为组合。</param>
    /// <returns>被按下按钮的编号；本类只用 <c>MB_OK</c>，返回值不参与决策。</returns>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string? lpText, string? lpCaption, uint uType);
}