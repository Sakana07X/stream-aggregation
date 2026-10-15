namespace StreamingAggregation.Tests.Fixtures;

/// <summary>
/// 源码断言测试的公共支撑：从仓库根读源文件，并从源码里切出指定方法/调用的片段。
/// </summary>
/// <remarks>
/// WinUI 与部分基础设施代码无法在测试宿主里加载（见 <c>CoreInterfaceCoverageTests</c>），
/// 于是"行为正确"退化成"源码里确实这么写"。共用这一份读取与切片逻辑，
/// 免得每条源码断言各抄一遍大括号配对扫描——抄错的副本自己不会报错，只会让断言悄悄失真。
/// </remarks>
internal static class RepositorySourceFile
{
    /// <summary>
    /// 读取仓库内的一个源文件。
    /// </summary>
    /// <param name="relativePath">相对仓库根的路径，用正斜杠分隔。</param>
    /// <returns>文件全文。</returns>
    public static string Read(string relativePath)
    {
        var root = RepositoryRootLocator.Find();
        Assert.True(
            root is not null,
            "未能从测试输出目录向上找到仓库根目录，无法核对源文件。");

        var sourcePath = Path.Combine(root!, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(sourcePath), $"源文件不存在：{relativePath}");

        return File.ReadAllText(sourcePath);
    }

    /// <summary>
    /// 从源码中切出以指定签名起始的方法体（按大括号配对）。
    /// </summary>
    /// <remarks>
    /// 只用于源码快照断言：方法体内插值字符串（如 <c>$"... {MinPort} ..."</c>）的大括号是配对的，
    /// 不会干扰配对计数。
    /// </remarks>
    /// <param name="source">源码全文。</param>
    /// <param name="signature">方法签名（在文件中唯一）。</param>
    /// <returns>方法体（不含签名与最外层大括号）；找不到时返回 <see langword="null"/>。</returns>
    public static string? ExtractMethodBody(string source, string signature)
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

        var closeIndex = FindCloseIndex(source, openIndex, '{', '}');
        return closeIndex < 0 ? null : source[(openIndex + 1)..closeIndex];
    }

    /// <summary>
    /// 从源码中切出以指定前缀起始的调用实参列表（按圆括号配对）。
    /// </summary>
    /// <remarks>
    /// 用来回答"这段赋值语句是不是发生在这个调用内部"这类问题。扫描按字符计数，
    /// 因此要求实参列表里不出现未配对的括号——本仓库的页面代码满足这一前提。
    /// </remarks>
    /// <param name="source">源码全文。</param>
    /// <param name="callPrefix">调用前缀（如 <c>_load.Apply(</c>）。</param>
    /// <returns>每个调用实参列表的起止下标（含首尾圆括号）。</returns>
    public static IReadOnlyList<(int Start, int End)> FindCallRanges(string source, string callPrefix)
    {
        var ranges = new List<(int Start, int End)>();

        for (var index = source.IndexOf(callPrefix, StringComparison.Ordinal);
             index >= 0;
             index = source.IndexOf(callPrefix, index + 1, StringComparison.Ordinal))
        {
            var openIndex = index + callPrefix.Length - 1;
            var closeIndex = FindCloseIndex(source, openIndex, '(', ')');

            if (closeIndex > 0)
            {
                ranges.Add((openIndex, closeIndex));
            }
        }

        return ranges;
    }

    /// <summary>
    /// 从 <paramref name="openIndex"/> 处起找到配对的闭括号。
    /// </summary>
    /// <param name="source">源码全文。</param>
    /// <param name="openIndex">开括号的字符下标。</param>
    /// <param name="open">开括号字符。</param>
    /// <param name="close">闭括号字符。</param>
    /// <returns>闭括号下标；未配对时返回 <c>-1</c>。</returns>
    private static int FindCloseIndex(string source, int openIndex, char open, char close)
    {
        var depth = 0;

        for (var i = openIndex; i < source.Length; i++)
        {
            if (source[i] == open)
            {
                depth++;
            }
            else if (source[i] == close)
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }
}