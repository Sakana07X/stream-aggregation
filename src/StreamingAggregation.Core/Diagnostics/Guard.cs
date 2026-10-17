using System.Runtime.CompilerServices;

namespace StreamingAggregation.Core.Diagnostics;

/// <summary>
/// 参数与生命周期的断言基座（Guard clause 基座）。
///
/// <para><b>为什么需要它。</b>本项目正从 .NET 10 迁移到 .NET Framework 4.8，
/// 而 .NET 6 才引入的 <c>ArgumentNullException.ThrowIf</c>、
/// <c>ArgumentException.ThrowIfNullOrEmpty</c>、
/// <c>ArgumentException.ThrowIfNullOrWhiteSpace</c>、
/// <c>ObjectDisposedException.ThrowIf</c> 在 net48 上<b>不存在</b>，
/// 且无法用 PolySharp 这类 source generator 补齐（它们是 BCL 已存在类型上的
/// 静态成员，源生成器只能补类型、不能给既有类型加成员）。
/// 于是由本类提供等价实现。</para>
///
/// <para><b>消息格式契约（实测结论，不是推测）。</b>本类只做两件事：
/// 自己拼出消息<b>正文</b>，其余交给 BCL 构造器。各层的一致性如下
/// （证据见 <c>.tmp/guard-msg-probe/</c> 的 .NET 10 实跑与
/// <c>.tmp/guard-parity/</c> 的 net48 实跑，测试见 <c>GuardTests</c>）：</para>
/// <list type="bullet">
///   <item><b>异常类型：完全一致。</b>抛的是<b>恰好</b>
///     <c>System.ArgumentNullException</c> / <c>System.ArgumentException</c> /
///     <c>System.ArgumentOutOfRangeException</c> / <c>System.ObjectDisposedException</c>，
///     不是派生类。这一层最关键：既有测试用的 xunit
///     <c>Assert.Throws&lt;T&gt;</c> 是<b>精确类型</b>匹配，派生类会直接失败。</item>
///   <item><b>ParamName / ObjectName：完全一致。</b>包括
///     <c>[CallerArgumentExpression]</c> 回填源码文本的行为。</item>
///   <item><b>消息正文：完全一致。</b>下面两条英文常量与 .NET 6/7/8/9/10 的
///     <c>System.SR</c> 资源逐字相同。</item>
///   <item><b>消息的参数名后缀：无法一致，且不可修。</b>net48 由 BCL 追加
///     <c>Environment.NewLine + 本地化的「参数名:」+ name</c>，
///     .NET Core 追加 <c>" (Parameter 'name')"</c>；前者还会随 UI 语言变
///     （中文系统上是「值不能为 null。」）。想强行对齐只能自定义派生异常覆写
///     <c>Message</c>，代价是 <c>Assert.Throws&lt;T&gt;</c> 全部失败 —— 得不偿失。
///     既有测试断言的是异常类型而非消息文本，因此不影响测试结果。</item>
/// </list>
///
/// <para><b>参数名契约。</b>迁移调用点时<b>原样保留</b>现有的 <c>paramName</c>
/// 字面量（包括手写字符串与 <c>nameof</c>），不要顺手"修正"：
/// 一是消息文本属于对外契约，二是 <c>nameof(draft)</c> 配 <c>draft.Summary</c>
/// 这类"看起来不匹配"的写法是上游有意为之。</para>
///
/// <para><b>CallerArgumentExpression 契约。</b>
/// <see cref="ThrowIfLessThan"/> / <see cref="ThrowIfGreaterThan"/> 与原生一样，
/// <c>paramName</c> 缺省时用 <c>[CallerArgumentExpression]</c> 回填<b>调用点
/// 写下的源码文本</b>。所以 <c>Guard.ThrowIfLessThan(count, min)</c> 报出的
/// 参数名是字面量 <c>"count"</c>，而如果调用点写成
/// <c>Guard.ThrowIfLessThan(GetCount(), min)</c>，参数名就会变成
/// <c>"GetCount()"</c> —— 这不是 bug，是原生同款行为。</para>
///
/// <para><b>与原生 API 的对应关系。</b></para>
/// <list type="table">
///   <item><term>Guard.NotNull(v, p)</term><description><c>ArgumentNullException.ThrowIfNull(v, p)</c></description></item>
///   <item><term>Guard.NotNullOrEmpty(v, p)</term><description><c>ArgumentException.ThrowIfNullOrEmpty(v, p)</c></description></item>
///   <item><term>Guard.NotNullOrWhiteSpace(v, p)</term><description><c>ArgumentException.ThrowIfNullOrWhiteSpace(v, p)</c></description></item>
///   <item><term>Guard.ThrowIfDisposed(b, n)</term><description><c>ObjectDisposedException.ThrowIf(b, instance)</c></description></item>
///   <item><term>Guard.NotDisposed(v, n)</term><description><c>ObjectDisposedException.ThrowIf(v is null, v)</c></description></item>
///   <item><term>Guard.ThrowIfLessThan(v, o, p)</term><description><c>ArgumentOutOfRangeException.ThrowIfLessThan(v, o, p)</c></description></item>
///   <item><term>Guard.ThrowIfGreaterThan(v, o, p)</term><description><c>ArgumentOutOfRangeException.ThrowIfGreaterThan(v, o, p)</c></description></item>
///   <item><term>Guard.ThrowIfLessThanOrEqual(v, o, p)</term><description><c>ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(v, o, p)</c></description></item>
///   <item><term>Guard.ThrowIfNegative(v, p)</term><description><c>ArgumentOutOfRangeException.ThrowIfNegative(v, p)</c></description></item>
/// </list>
/// <para>方法集按<b>本仓库实际用到的调用点</b>裁剪，没有把
/// <c>ArgumentOutOfRangeException</c> 的 8 个 <c>ThrowIf*</c> 全搬过来：
/// 用不到的成员只会增加要维护的面。目前已覆盖全部 12 个越界断言调用点。</para>
/// </summary>
public static class Guard
{
    // 下面两条文案与 System.SR 内部资源表逐字一致，直接内联，避免 net48 上
    // 为拿一条常量而引入 System.Resources.Extensions。
    //
    // "Value cannot be null." 故意不在这里内联：它属于 ArgumentNullException 自己的
    // BCL 资源，`new ArgumentNullException(paramName)` 自带。改写成
    // `new ArgumentException("Value cannot be null.", paramName)` 会同时错两处——
    // 异常类型变成 ArgumentException，消息也会多出 BCL 追加的参数名后缀。
    private const string EmptyOrWhiteSpace =
        "The value cannot be an empty string or composed entirely of whitespace.";
    private const string EmptyString = "The value cannot be an empty string.";
    private const string GreaterThanOrEqualTo = "must be greater than or equal to";
    private const string LessThanOrEqualTo = "must be less than or equal to";
    private const string GreaterThan = "must be greater than";
    private const string NonNegative = "must be a non-negative value";

    /// <summary>
    /// 断言引用类型参数非 <see langword="null"/>，否则抛出
    /// <see cref="ArgumentNullException"/>。
    /// </summary>
    /// <typeparam name="T">引用类型。</typeparam>
    /// <param name="value">待校验值。</param>
    /// <param name="paramName">
    /// 参数名。为 <see langword="null"/> 时消息不带 <c>(Parameter '...')</c> 后缀。
    /// </param>
    /// <returns>非空时的原值，便于写成 <c>var x = Guard.NotNull(y);</c>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> 为 <see langword="null"/>。</exception>
    public static T NotNull<T>(T? value, string? paramName = null)
        where T : class
    {
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }

        return value;
    }

    /// <summary>
    /// 断言字符串非 <see langword="null"/> 且不含纯空白，否则抛出
    /// <see cref="ArgumentNullException"/>（空串分支为
    /// <see cref="ArgumentException"/>）。
    /// </summary>
    /// <remarks>
    /// 与原生 <c>ArgumentException.ThrowIfNullOrWhiteSpace</c> 的一个易错点一致：
    /// <b>null 抛的是 <see cref="ArgumentNullException"/> 而不是
    /// <see cref="ArgumentException"/></b>，消息是 "Value cannot be null."。
    /// 两个类型共享 <c>ArgumentException</c> 基类，所以
    /// <c>catch (ArgumentException)</c> 仍然接得住，但
    /// <c>Assert.ThrowsAsync&lt;ArgumentNullException&gt;</c> 才匹配原生。
    /// </remarks>
    /// <param name="value">待校验值。</param>
    /// <param name="paramName">参数名。</param>
    /// <returns>非空且非纯空白时的原值。</returns>
    public static string NotNullOrWhiteSpace(string? value, string? paramName = null)
    {
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }

        // net48 的 string.IsNullOrWhiteSpace 与 .NET Core 语义完全一致
        // （null 或全为 char.IsWhiteSpace 字符即视为空白），直接复用，
        // 避免自己实现一遍 Unicode 空白判定导致两边判定漂移。
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(EmptyOrWhiteSpace, paramName);
        }

        return value;
    }

    /// <summary>
    /// 断言字符串非 <see langword="null"/> 且非空串，否则抛出
    /// <see cref="ArgumentNullException"/>（空串分支为
    /// <see cref="ArgumentException"/>）。
    /// </summary>
    /// <param name="value">待校验值。</param>
    /// <param name="paramName">参数名。</param>
    /// <returns>非空时的原值。</returns>
    public static string NotNullOrEmpty(string? value, string? paramName = null)
    {
        if (value is null)
        {
            throw new ArgumentNullException(paramName);
        }

        if (value.Length == 0)
        {
            throw new ArgumentException(EmptyString, paramName);
        }

        return value;
    }

    /// <summary>
    /// 断言对象尚未被释放。消息由 <see cref="ObjectDisposedException"/> 组装：
    /// 基础句 + （<paramref name="objectName"/> 非空时）
    /// <c>Environment.NewLine + 对象名部分</c>，与原生 <c>ThrowIf</c> 同规则。
    /// </summary>
    /// <remarks>
    /// 契约：<paramref name="value"/> 为 <see langword="null"/> 就判定"已释放"。
    /// CLR 无法从外部观察一个对象是否已被 <c>Dispose</c>，所以这个方法只能以
    /// "引用被清空"作为可检测的死亡信号。若持有的是显式的 <c>bool</c> 释放标志
    /// （本项目 OneBotWebSocketClient / TestWebSocketServer 就是这种形态），
    /// 请改用 <see cref="ThrowIfDisposed"/>，它与原生
    /// <c>ObjectDisposedException.ThrowIf(condition, instance)</c> 一一对应。
    /// </remarks>
    /// <param name="value">待校验对象引用。</param>
    /// <param name="objectName">对象名，通常是 <c>nameof(X)</c>；为 <see langword="null"/> 时消息不带对象名。</param>
    /// <returns>未释放时的原值。</returns>
    /// <exception cref="ObjectDisposedException"><paramref name="value"/> 为 <see langword="null"/>。</exception>
    public static T NotDisposed<T>(T? value, string? objectName)
        where T : class
    {
        if (value is null)
        {
            throw new ObjectDisposedException(objectName);
        }

        return value;
    }

    /// <summary>
    /// <see cref="ObjectDisposedException.ThrowIf(bool, object)"/> 的等价物：
    /// <paramref name="disposed"/> 为 <see langword="true"/> 时抛
    /// <see cref="ObjectDisposedException"/>。
    /// </summary>
    /// <remarks>
    /// 原生重载的第二个参数是对象实例，消息里的对象名由
    /// <c>instance.GetType().FullName</c> 推导。本项目现有调用点传的都是
    /// <c>this</c> 且期望消息里出现实例的类型全名，因此显式暴露
    /// <paramref name="objectName"/> 以保持迁移前后消息一致；
    /// 调用点写 <c>GetType().FullName</c> 即可与原生完全等价。
    /// </remarks>
    /// <param name="disposed">是否已释放。</param>
    /// <param name="objectName">对象名；为 <see langword="null"/> 时消息不带对象名。</param>
    public static void ThrowIfDisposed(bool disposed, string? objectName)
    {
        if (disposed)
        {
            throw new ObjectDisposedException(objectName);
        }
    }

    /// <summary>
    /// <see cref="ArgumentOutOfRangeException.ThrowIfLessThan{T}(T, T, string?)"/>
    /// 的等价物：<paramref name="value"/> 小于 <paramref name="other"/> 时抛
    /// <see cref="ArgumentOutOfRangeException"/>。
    /// </summary>
    /// <typeparam name="T">可比较类型。</typeparam>
    /// <param name="value">待校验值。</param>
    /// <param name="other">下界。</param>
    /// <param name="paramName">
    /// 参数名；缺省时由 <c>[CallerArgumentExpression]</c> 取调用点源码文本，
    /// 与原生行为一致。
    /// </param>
    public static void ThrowIfLessThan<T>(
        T value,
        T other,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (Comparer<T>.Default.Compare(value, other) < 0)
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, $"{paramName} ('{value}') {GreaterThanOrEqualTo} '{other}'.");
        }
    }

    /// <summary>
    /// <see cref="ArgumentOutOfRangeException.ThrowIfGreaterThan{T}(T, T, string?)"/>
    /// 的等价物：<paramref name="value"/> 大于 <paramref name="other"/> 时抛
    /// <see cref="ArgumentOutOfRangeException"/>。
    /// </summary>
    /// <typeparam name="T">可比较类型。</typeparam>
    /// <param name="value">待校验值。</param>
    /// <param name="other">上界。</param>
    /// <param name="paramName">
    /// 参数名；缺省时由 <c>[CallerArgumentExpression]</c> 取调用点源码文本，
    /// 与原生行为一致。
    /// </param>
    public static void ThrowIfGreaterThan<T>(
        T value,
        T other,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (Comparer<T>.Default.Compare(value, other) > 0)
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, $"{paramName} ('{value}') {LessThanOrEqualTo} '{other}'.");
        }
    }

    /// <summary>
    /// <see cref="ArgumentOutOfRangeException.ThrowIfLessThanOrEqual{T}(T, T, string?)"/>
    /// 的等价物：<paramref name="value"/> 小于或等于 <paramref name="other"/> 时抛
    /// <see cref="ArgumentOutOfRangeException"/>。
    /// </summary>
    /// <remarks>
    /// 注意与 <see cref="ThrowIfGreaterThan{T}"/> 的区别：原生这两个方法方向相反
    /// （<c>ThrowIfLessThanOrEqual(v, o)</c> 判的是 <c>v &lt;= o</c>，
    /// 报「must be greater than」），不是「<c>ThrowIfLessThan</c> 但取等」，
    /// 迁移时最容易搞反、也最容易被测试漏掉。
    /// </remarks>
    /// <typeparam name="T">可比较类型。</typeparam>
    /// <param name="value">待校验值。</param>
    /// <param name="other">下界。</param>
    /// <param name="paramName">
    /// 参数名；缺省时由 <c>[CallerArgumentExpression]</c> 取调用点源码文本。
    /// </param>
    public static void ThrowIfLessThanOrEqual<T>(
        T value,
        T other,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (Comparer<T>.Default.Compare(value, other) <= 0)
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, $"{paramName} ('{value}') {GreaterThan} '{other}'.");
        }
    }

    /// <summary>
    /// <see cref="ArgumentOutOfRangeException.ThrowIfNegative{T}(T, string?)"/>
    /// 的等价物：<paramref name="value"/> 为负数时抛
    /// <see cref="ArgumentOutOfRangeException"/>。0 是合法值。
    /// </summary>
    /// <typeparam name="T">可比较类型。</typeparam>
    /// <param name="value">待校验值。</param>
    /// <param name="paramName">
    /// 参数名；缺省时由 <c>[CallerArgumentExpression]</c> 取调用点源码文本。
    /// </param>
    public static void ThrowIfNegative<T>(
        T value,
        [CallerArgumentExpression(nameof(value))] string? paramName = null)
    {
        if (Comparer<T>.Default.Compare(value, default!) < 0)
        {
            throw new ArgumentOutOfRangeException(
                paramName, value, $"{paramName} ('{value}') {NonNegative}.");
        }
    }
}
