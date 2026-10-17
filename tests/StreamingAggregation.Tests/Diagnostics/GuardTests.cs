using System.Globalization;
using StreamingAggregation.Core.Diagnostics;

namespace StreamingAggregation.Tests.Diagnostics;

/// <summary>
/// <see cref="Guard"/> 测试。
///
/// <para><b>这份测试要防的是什么。</b>Guard 存在的唯一理由，是替代 net48 上不存在的
/// <c>ArgumentNullException.ThrowIfNull</c> 等原生 API。替代品最怕的是
/// 「异常类型对、消息不对」——类型决定 <c>catch</c> 和 <c>Assert.Throws</c> 是否还成立，
/// 消息决定日志、告警与跨语言环境的稳定性。两层都要钉死。</para>
///
/// <para><b>为什么消息断言写成「与 BCL 构造器同形」而不是硬编码英文字面量。</b>
/// .NET Framework 4.8 的异常文案来自 mscorlib 的本地化资源，会随
/// <see cref="CultureInfo.CurrentUICulture"/> 变（中文系统上是「值不能为 null。」）；
/// 同时 net48 与 .NET Core 的<b>结构</b>也不同：net48 是
/// <c>消息 + 换行 + 本地化的「参数名:」</c>，.NET Core 是
/// <c>消息 + " (Parameter '...')"</c>。因此本文件分三层断言：
/// <list type="bullet">
///   <item>异常<b>类型</b>与 <b>ParamName/ObjectName</b>：逐字断言，这一层与原生完全一致；</item>
///   <item>消息<b>正文</b>（Guard 自己写的那半句）：用英文字面量断言，
///         这部分与 .NET 6/7/8/9/10 原生逐字一致，也是跨语言环境唯一稳定的部分；</item>
///   <item>消息<b>全文</b>：与「同样参数喂给 BCL 构造器」的结果对比 —— 既证明 Guard
///         没私自加/减任何东西，又不会因为跑在中文机器上而误报。</item>
/// </list>
/// 另外有一组 <c>KnownDifference_*</c> 测试把「net48 与 .NET Core 的差异」
/// 写成显式契约，避免以后有人把这个差异当成 bug 顺手改掉。</para>
///
/// <para>黄金文本抓取过程见 <c>.tmp/guard-msg-probe/</c>（.NET 10 实跑）与
/// <c>.tmp/guard-parity/</c>（net48 实跑）。</para>
/// </summary>
public sealed class GuardTests
{
    // 与 System.SR 内部资源表逐字一致（.NET 6/7/8/9/10 原生 ThrowIf* 使用的文案）。
    private const string WhiteSpaceText =
        "The value cannot be an empty string or composed entirely of whitespace.";
    private const string EmptyText = "The value cannot be an empty string.";

    private static object NullObject => null!;
    private static string? NullString => null;

    /// <summary>把样本串转成可读的 U+XXXX 序列，仅用于失败信息。</summary>
    private static string ToCodePoints(string value)
        => string.Join(" U+", value.Select(static c => ((int)c).ToString("X4", CultureInfo.InvariantCulture)));

    // ---------------------------------------------------------------- NotNull

    /// <summary>
    /// 类型必须是 <b>恰好</b> <see cref="ArgumentNullException"/>：既有测试用的是
    /// xunit 的 <c>Assert.Throws&lt;ArgumentNullException&gt;</c>，而它做的是
    /// <b>精确类型</b>匹配（派生类会失败），所以 Guard 不允许抛子类。
    /// </summary>
    [Fact]
    public void NotNull_ThrowsExactlyArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => Guard.NotNull(NullObject, "settings"));

        Assert.Equal("settings", ex.ParamName);
    }

    /// <summary>paramName 为 null 时不能凭空造出参数名。</summary>
    [Fact]
    public void NotNull_WithoutParamName_LeavesParamNameEmpty()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => Guard.NotNull(NullObject));

        Assert.True(string.IsNullOrEmpty(ex.ParamName), $"ParamName 应为空，实际为 '{ex.ParamName}'。");
    }

    /// <summary>消息全文与「同样参数喂给 BCL」完全一致（证明 Guard 没插手）。</summary>
    [Fact]
    public void NotNull_MessageMatchesBclComposition()
    {
        var actual = Assert.Throws<ArgumentNullException>(() => Guard.NotNull(NullObject, "settings"));
        var expected = new ArgumentNullException("settings");

        Assert.Equal(expected.Message, actual.Message);
    }

    /// <summary>正常路径原样返回同一个引用，便于写成 <c>var x = Guard.NotNull(y);</c>。</summary>
    [Fact]
    public void NotNull_ReturnsSameReferenceWhenValid()
    {
        var value = new object();

        Assert.Same(value, Guard.NotNull(value));
        Assert.Same(value, Guard.NotNull(value, "value"));
    }

    /// <summary>泛型约束是 <c>class</c>，任意引用类型都可用。</summary>
    [Fact]
    public void NotNull_AcceptsAnyReferenceType()
    {
        var value = new List<int>();

        Assert.Same(value, Guard.NotNull(value, "value"));
    }

    // ----------------------------------------------------- NotNullOrWhiteSpace

    /// <summary>
    /// 关键陷阱：原生 <c>ArgumentException.ThrowIfNullOrWhiteSpace(null)</c> 抛的是
    /// <see cref="ArgumentNullException"/>（不是 ArgumentException）。
    /// 两者共享基类，<c>catch (ArgumentException)</c> 都接得住，
    /// 但 <c>Assert.Throws&lt;ArgumentException&gt;</c> 只接得住后者。必须钉死。
    /// </summary>
    [Fact]
    public void NotNullOrWhiteSpace_Null_ThrowsExactlyArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => Guard.NotNullOrWhiteSpace(NullString, "id"));

        Assert.Equal("id", ex.ParamName);
    }

    /// <summary>空白串抛的是 <see cref="ArgumentException"/>（精确类型）。</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void NotNullOrWhiteSpace_Whitespace_ThrowsExactlyArgumentException(string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => Guard.NotNullOrWhiteSpace(value, "id"));

        Assert.Equal("id", ex.ParamName);
        Assert.StartsWith(WhiteSpaceText, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>消息正文与原生逐字一致。</summary>
    [Fact]
    public void NotNullOrWhiteSpace_MessageBodyMatchesNative()
    {
        var ex = Assert.Throws<ArgumentException>(() => Guard.NotNullOrWhiteSpace(" ", "id"));
        var expected = new ArgumentException(WhiteSpaceText, "id");

        Assert.Equal(expected.Message, ex.Message);
    }

    /// <summary>合法值原样返回。</summary>
    [Fact]
    public void NotNullOrWhiteSpace_ReturnsSameValueWhenValid()
    {
        Assert.Equal("ok", Guard.NotNullOrWhiteSpace("ok", "id"));
    }

    /// <summary>
    /// 空白判定必须与 <see cref="string.IsNullOrWhiteSpace"/> 逐个样本一致。
    /// 自己写一遍 <c>char.IsWhiteSpace</c> 循环几乎必然在某个 Unicode 边界上漂移
    /// （NBSP / EM SPACE / 全角空格 / BOM 都是经典陷阱），所以直接复用 BCL。
    /// </summary>
    [Fact]
    public void NotNullOrWhiteSpace_ClassifiesExactlyLikeStringIsNullOrWhiteSpace()
    {
        string[] samples =
        [
            string.Empty,
            " ",
            "   ",
            "\t",
            "\r",
            "\n",
            "\v",
            "\f",
            "\t\r\n ",
            "a",
            " a ",
            "0",
            "\u00a0",
            "\u1680",
            "\u2000",
            "\u2003",
            "\u205f",
            "\u3000",
            "\u2028",
            "\u2029",
            "\ufeff",
            "\0",
            "\u0001",
            "\u001f",
            "\u007f",
        ];

        foreach (string sample in samples)
        {
            bool bclSaysWhitespace = string.IsNullOrWhiteSpace(sample);
            bool guardThrew = false;
            try
            {
                Guard.NotNullOrWhiteSpace(sample, "p");
            }
            catch (ArgumentException)
            {
                guardThrew = true;
            }

            Assert.True(
                bclSaysWhitespace == guardThrew,
                "空白判定与 string.IsNullOrWhiteSpace 不一致：样本 \"" + ToCodePoints(sample)
                + "\"，BCL=" + bclSaysWhitespace + "，Guard=" + guardThrew);
        }
    }

    // --------------------------------------------------------- NotNullOrEmpty

    /// <summary>null 分支抛 <see cref="ArgumentNullException"/>，与原生一致。</summary>
    [Fact]
    public void NotNullOrEmpty_Null_ThrowsExactlyArgumentNullException()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => Guard.NotNullOrEmpty(NullString, "id"));

        Assert.Equal("id", ex.ParamName);
    }

    /// <summary>空串抛 <see cref="ArgumentException"/>，且消息正文与原生逐字一致。</summary>
    [Fact]
    public void NotNullOrEmpty_Empty_ThrowsExactlyArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(() => Guard.NotNullOrEmpty(string.Empty, "id"));

        Assert.Equal("id", ex.ParamName);
        Assert.StartsWith(EmptyText, ex.Message, StringComparison.Ordinal);
        Assert.Equal(new ArgumentException(EmptyText, "id").Message, ex.Message);
    }

    /// <summary>
    /// 只有空串算非法，纯空白合法 —— 这是与 NotNullOrWhiteSpace 的关键区别，
    /// 两者混淆会让「一个空格」这种合法值被误拒。
    /// </summary>
    [Fact]
    public void NotNullOrEmpty_AcceptsWhitespaceOnlyString()
    {
        Assert.Equal(" ", Guard.NotNullOrEmpty(" ", "id"));
        Assert.Equal("\t", Guard.NotNullOrEmpty("\t", "id"));
    }

    // ------------------------------------------- NotDisposed / ThrowIfDisposed

    /// <summary>
    /// 对象名缺失时消息只有基础句，没有任何对象名后缀。
    /// <para>这里不硬编码英文：net48 的基础句来自 mscorlib 资源，会随 UI 语言变。
    /// 改成「有对象名的消息去掉后缀后必须等于无对象名的消息」来做等价断言，
    /// 这样既证明了后缀的有无，又与语言无关。</para>
    /// </summary>
    [Fact]
    public void NotDisposed_WithoutObjectName_MessageHasNoObjectSuffix()
    {
        var withoutName = Assert.Throws<ObjectDisposedException>(
            () => Guard.NotDisposed(NullObject, objectName: null));
        var withName = Assert.Throws<ObjectDisposedException>(() => Guard.NotDisposed(NullObject, "Widget"));

        Assert.True(string.IsNullOrEmpty(withoutName.ObjectName), "无对象名时 ObjectName 应为空。");
        Assert.Equal("Widget", withName.ObjectName);
        Assert.Equal(withoutName.Message, withName.Message.Substring(0, withoutName.Message.Length));
        Assert.True(
            withName.Message.Length > withoutName.Message.Length,
            "带对象名的消息应当比不带的长（即确实追加了对象名后缀）。");
        // 分隔符用的是 Environment.NewLine，与 .NET Core 一致。
        Assert.Contains(Environment.NewLine, withName.Message, StringComparison.Ordinal);
    }

    /// <summary>有对象名时消息全文与「同样参数喂给 BCL 构造器」完全一致。</summary>
    [Fact]
    public void NotDisposed_WithObjectName_MessageMatchesNative()
    {
        var ex = Assert.Throws<ObjectDisposedException>(() => Guard.NotDisposed(NullObject, "Widget"));
        var expected = new ObjectDisposedException("Widget");

        Assert.Equal("Widget", ex.ObjectName);
        Assert.Equal(expected.Message, ex.Message);
    }

    /// <summary><see cref="Guard.ThrowIfDisposed"/> 是原生 <c>ThrowIf(bool, object)</c> 的等价物。</summary>
    [Fact]
    public void ThrowIfDisposed_ThrowsOnlyWhenTrue()
    {
        Assert.Throws<ObjectDisposedException>(() => Guard.ThrowIfDisposed(true, "Client"));

        Assert.Null(Record.Exception(() => Guard.ThrowIfDisposed(false, "Client")));
        Assert.Null(Record.Exception(() => Guard.ThrowIfDisposed(false, objectName: null)));
    }

    /// <summary>未释放时原样返回引用。</summary>
    [Fact]
    public void NotDisposed_ReturnsSameReferenceWhenAlive()
    {
        var value = new object();

        Assert.Same(value, Guard.NotDisposed(value, "Sample"));
    }

    // -------------------------------------------- ThrowIfLessThan/GreaterThan

    /// <summary>越界抛的必须是 <b>恰好</b> <see cref="ArgumentOutOfRangeException"/>。</summary>
    [Fact]
    public void ThrowIfLessThan_ThrowsExactlyArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Guard.ThrowIfLessThan(1, 5, "count"));

        Assert.Equal("count", ex.ParamName);
        // Guard 自己写的正文与原生逐字一致（不随 UI 语言变）。
        Assert.StartsWith("count ('1') must be greater than or equal to '5'.", ex.Message, StringComparison.Ordinal);
        // 「Actual value was ...」那段由 BCL 追加、会被本地化，所以改用 ActualValue 属性断言。
        Assert.Equal(1, ex.ActualValue);
    }

    /// <summary>上界方向同理。</summary>
    [Fact]
    public void ThrowIfGreaterThan_ThrowsExactlyArgumentOutOfRangeException()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Guard.ThrowIfGreaterThan(9, 5, "count"));

        Assert.Equal("count", ex.ParamName);
        Assert.StartsWith("count ('9') must be less than or equal to '5'.", ex.Message, StringComparison.Ordinal);
        Assert.Equal(9, ex.ActualValue);
    }

    /// <summary>边界值（等于界）合法，与原生 <c>ThrowIfLessThan</c> 的严格小于一致。</summary>
    [Fact]
    public void ThrowIfLessThanAndGreaterThan_AllowEqualBoundary()
    {
        Assert.Null(Record.Exception(() => Guard.ThrowIfLessThan(5, 5, "count")));
        Assert.Null(Record.Exception(() => Guard.ThrowIfGreaterThan(5, 5, "count")));
        Assert.Null(Record.Exception(() => Guard.ThrowIfLessThan(6, 5, "count")));
        Assert.Null(Record.Exception(() => Guard.ThrowIfGreaterThan(4, 5, "count")));
    }

    /// <summary>
    /// <c>[CallerArgumentExpression]</c> 行为必须与原生一致：不给 paramName 时，
    /// 参数名就是调用点写下的<b>源码文本</b>，而不是方法内部能推导出的任何东西。
    /// </summary>
    [Fact]
    public void ThrowIfLessThan_InfersParamNameFromSourceText()
    {
        int count = 1;

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Guard.ThrowIfLessThan(count, 5));

        Assert.Equal("count", ex.ParamName);
    }

    /// <summary>非数字类型（TimeSpan）走 <see cref="IComparable{T}"/> 路径也能工作。</summary>
    [Fact]
    public void ThrowIfGreaterThan_WorksForNonNumericComparable()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => Guard.ThrowIfGreaterThan(TimeSpan.FromHours(1), TimeSpan.FromMinutes(30), "window"));

        Assert.Equal("window", ex.ParamName);
        Assert.Contains("01:00:00", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>方向陷阱。</b><c>ThrowIfLessThanOrEqual</c> 判的是 <c>v &lt;= o</c>，
    /// 报的却是「must be greater than」。它<b>不是</b>「<c>ThrowIfLessThan</c> 但取等」，
    /// 迁移时最容易搞反，单测也最容易漏掉 —— 这里把两个方向钉在一起。
    /// </summary>
    [Fact]
    public void ThrowIfLessThanOrEqual_ThrowsWhenEqual_AndReportsGreaterThan()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Guard.ThrowIfLessThanOrEqual(5, 5, "count"));

        Assert.Equal("count", ex.ParamName);
        Assert.StartsWith("count ('5') must be greater than '5'.", ex.Message, StringComparison.Ordinal);
        Assert.Equal(5, ex.ActualValue);

        // 严格大于才合法 —— 越界条件是 <=。
        Assert.Null(Record.Exception(() => Guard.ThrowIfLessThanOrEqual(6, 5, "count")));
        Assert.Throws<ArgumentOutOfRangeException>(() => Guard.ThrowIfLessThanOrEqual(4, 5, "count"));
    }

    /// <summary>
    /// <c>ThrowIfLessThan</c> 与 <c>ThrowIfLessThanOrEqual</c> 互为补集，
    /// 合起来覆盖「小于」与「小于等于」两种判定，迁移时不会漏边界。
    /// </summary>
    [Fact]
    public void ThrowIfLessThanAndLessThanOrEqual_AreComplementary()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Guard.ThrowIfLessThan(4, 5, "count"));
        Assert.Null(Record.Exception(() => Guard.ThrowIfLessThan(5, 5, "count")));

        Assert.Throws<ArgumentOutOfRangeException>(() => Guard.ThrowIfLessThanOrEqual(5, 5, "count"));
        Assert.Null(Record.Exception(() => Guard.ThrowIfLessThanOrEqual(6, 5, "count")));
    }

    /// <summary><c>ThrowIfNegative</c>：0 合法，只有负数抛。</summary>
    [Fact]
    public void ThrowIfNegative_AllowsZero()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Guard.ThrowIfNegative(-3, "count"));

        Assert.Equal("count", ex.ParamName);
        Assert.StartsWith("count ('-3') must be a non-negative value.", ex.Message, StringComparison.Ordinal);
        Assert.Equal(-3, ex.ActualValue);

        Assert.Null(Record.Exception(() => Guard.ThrowIfNegative(0, "count")));
        Assert.Null(Record.Exception(() => Guard.ThrowIfNegative(3, "count")));
    }

    /// <summary>缺省参数名同样走 <c>[CallerArgumentExpression]</c>。</summary>
    [Fact]
    public void ThrowIfNegative_InfersParamNameFromSourceText()
    {
        int delta = -1;

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Guard.ThrowIfNegative(delta));

        Assert.Equal("delta", ex.ParamName);
    }

    // ---------------------------------------------------------------------
    // 以下是「net48 与 .NET Core 的已知差异」显式断言。
    // 目的：把差异钉成契约，将来有人改动 Guard 时会立刻看到，而不是悄悄改掉。
    // ---------------------------------------------------------------------

    /// <summary>
    /// <b>已知差异 A：基础句被本地化。</b>
    /// Guard 传进 BCL 的英文文案在中文机器上依然原样保留（正文断言已覆盖），
    /// 但 <c>ArgumentNullException</c> 自己的基础文案来自 mscorlib 资源，会被本地化
    /// （中文系统上是「值不能为 null。」）。.NET Framework 固有行为，.NET Core 恒定英文。
    /// </summary>
    [Fact]
    public void KnownDifference_NullClauseIsLocalizedByFramework()
    {
        string actual = new ArgumentNullException("p").Message;
        string baseClause = new ArgumentNullException().Message;

        Assert.NotNull(baseClause);
        Assert.StartsWith(baseClause, actual, StringComparison.Ordinal);
        Assert.True(actual.Length > baseClause.Length, "传了 paramName 时消息应当比没传更长。");
    }

    /// <summary>
    /// <b>已知差异 B：参数名后缀的结构。</b>
    /// net48 是 <c>消息 + 换行 + 本地化的「参数名:」+ name</c>，
    /// .NET Core 是 <c>消息 + " (Parameter 'name')"</c>。
    /// 既有测试断言的是异常<b>类型</b>而非消息文本，所以这个结构差异不影响测试结果；
    /// 若将来确实需要与 .NET Core 逐字一致，只能自定义派生异常并覆写 Message，
    /// 但那会让 xunit 的 <c>Assert.Throws&lt;T&gt;</c>（精确类型匹配）失败 —— 取舍见报告。
    /// </summary>
    [Fact]
    public void KnownDifference_ParamNameSuffixStructureDiffersFromDotNetCore()
    {
        string actual = new ArgumentNullException("p").Message;
        string expectedOnDotNetCore = "Value cannot be null. (Parameter 'p')";

        // 两种 UI 文化下 net48 都不会产出 .NET Core 那种结构。
        Assert.NotEqual(expectedOnDotNetCore, actual);
    }

    /// <summary>
    /// <b>已知差异 C：对象名后缀的标签与引号被本地化。</b>
    /// 结构（换行 + 「对象名 + 引号 + x + 引号 + 句号」）两边一致，
    /// 但标签与引号形状随 UI 语言变：英文是 <c>Object name: 'x'.</c>，
    /// 中文是 <c>对象名:“x”。</c>。因此不能硬编码英文，只能断言结构事实。
    /// </summary>
    [Fact]
    public void KnownDifference_ObjectNameSuffixIsLocalizedButSameStructure()
    {
        var ex = new ObjectDisposedException("Widget");

        Assert.Equal("Widget", ex.ObjectName);
        Assert.Contains("Widget", ex.Message, StringComparison.Ordinal);
        Assert.Contains(Environment.NewLine, ex.Message, StringComparison.Ordinal);
    }
}