using System.Globalization;
using StreamingAggregation.Core.Diagnostics;

namespace StreamingAggregation.Infrastructure.OneBot;

/// <summary>
/// 断线重连退避策略：指数退避（Exponential Backoff）+ 抖动（Jitter）+ 上限（Cap）。
/// </summary>
/// <remarks>
/// 本类型<strong>无状态且线程安全</strong>：延迟由「失败次数」这一纯输入决定，
/// 不保存任何计数器，因此同一个失败序号总是得到同一基准延迟，便于测试断言。
/// 抖动所需的随机源在内部加锁，可安全并发调用。
/// </remarks>
public sealed class ReconnectPolicy
{
    /// <summary>默认策略：初始 1 秒、倍数 2、上限 30 秒、±20% 抖动、无限重试。</summary>
    public static ReconnectPolicy Default { get; } = new();

    private readonly Random _random;
    private readonly object _randomGate = new();

    /// <summary>
    /// 进程内共享的默认随机源。net48 没有 <c>Random.Shared</c>（.NET 6+），
    /// 用一份静态实例顶上：<see cref="Random"/> 在 net48 上不是线程安全的，
    /// 而所有读取都在 <see cref="_randomGate"/> 保护下进行，因此共享是安全的。
    /// </summary>
    private static readonly Random SharedRandom = new();

    /// <summary>
    /// 初始化退避策略。
    /// </summary>
    /// <param name="initialDelay">首次失败后的基准等待时间，默认 1 秒。</param>
    /// <param name="maximumDelay">单次等待上限，默认 30 秒。</param>
    /// <param name="multiplier">每次失败后延迟的放大倍数，须 ≥ 1，默认 2。</param>
    /// <param name="jitterFactor">抖动比例，取值区间 [0, 1]，表示 ±20% 即 0.2。</param>
    /// <param name="maxAttempts">最大连续失败次数；0 表示无限重试。</param>
    /// <param name="random">抖动随机源，测试可注入固定种子；不传时用进程内共享的 <see cref="Random"/> 实例。</param>
    /// <exception cref="ArgumentOutOfRangeException">参数越界。</exception>
    public ReconnectPolicy(
        TimeSpan? initialDelay = null,
        TimeSpan? maximumDelay = null,
        double multiplier = 2.0,
        double jitterFactor = 0.2,
        int maxAttempts = 0,
        Random? random = null)
    {
        InitialDelay = initialDelay ?? TimeSpan.FromSeconds(1);
        MaximumDelay = maximumDelay ?? TimeSpan.FromSeconds(30);
        Multiplier = multiplier;
        JitterFactor = jitterFactor;
        MaxAttempts = maxAttempts;

        if (InitialDelay <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay), InitialDelay, "初始延迟必须大于零。");
        }

        if (MaximumDelay < InitialDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDelay), MaximumDelay, "延迟上限不得小于初始延迟。");
        }

        if (multiplier < 1.0 || double.IsNaN(multiplier) || double.IsInfinity(multiplier))
        {
            throw new ArgumentOutOfRangeException(nameof(multiplier), multiplier, "退避倍数必须是 ≥ 1 的有限数值。");
        }

        if (jitterFactor is < 0.0 or > 1.0 || double.IsNaN(jitterFactor))
        {
            throw new ArgumentOutOfRangeException(nameof(jitterFactor), jitterFactor, "抖动比例必须位于 [0, 1] 区间。");
        }

        Guard.ThrowIfNegative(maxAttempts);

        _random = random ?? SharedRandom;
    }

    /// <summary>首次失败后的基准等待时间。</summary>
    public TimeSpan InitialDelay { get; }

    /// <summary>单次等待的硬上限，抖动后也不会超过该值。</summary>
    public TimeSpan MaximumDelay { get; }

    /// <summary>指数退避倍数。</summary>
    public double Multiplier { get; }

    /// <summary>抖动比例，[0, 1]；0 表示不抖动。</summary>
    public double JitterFactor { get; }

    /// <summary>最大连续失败次数；0 表示无限重试。</summary>
    public int MaxAttempts { get; }

    /// <summary>
    /// 按连续失败次数计算「含抖动」的实际等待时间。
    /// </summary>
    /// <param name="consecutiveFailures">已连续失败的次数，0 表示第一次失败。</param>
    /// <returns>介于零与 <see cref="MaximumDelay"/> 之间的等待时间。</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="consecutiveFailures"/> 为负数。</exception>
    public TimeSpan GetDelay(int consecutiveFailures)
    {
        Guard.ThrowIfNegative(consecutiveFailures);
        return ApplyJitter(CalculateBaseDelay(consecutiveFailures));
    }

    /// <summary>
    /// 按连续失败次数计算基准等待时间（不含抖动），供测试断言使用。
    /// </summary>
    /// <param name="consecutiveFailures">已连续失败的次数，0 表示第一次失败。</param>
    /// <returns>介于 <see cref="InitialDelay"/> 与 <see cref="MaximumDelay"/> 之间的延迟。</returns>
    public TimeSpan CalculateBaseDelay(int consecutiveFailures)
    {
        Guard.ThrowIfNegative(consecutiveFailures);

        var cap = MaximumDelay.TotalMilliseconds;
        var delay = InitialDelay.TotalMilliseconds;

        // 迭代相乘而非 Math.Pow：连续失败上万次时既不会溢出，也会因触及上限而提前退出。
        for (var i = 0; i < consecutiveFailures && delay < cap; i++)
        {
            delay *= Multiplier;
        }

        return TimeSpan.FromMilliseconds(Math.Min(delay, cap));
    }

    /// <summary>
    /// 在基准延迟上叠加对称抖动，并把结果钳制到 [0, <see cref="MaximumDelay"/>]。
    /// </summary>
    /// <param name="baseDelay">基准延迟。</param>
    /// <returns>抖动后的等待时间。</returns>
    public TimeSpan ApplyJitter(TimeSpan baseDelay)
    {
        if (baseDelay <= TimeSpan.Zero || JitterFactor <= 0.0)
        {
            return baseDelay;
        }

        double uniform;

        lock (_randomGate)
        {
            uniform = _random.NextDouble();
        }

        var factor = (1.0 - JitterFactor) + (2.0 * JitterFactor * uniform);
        var jittered = baseDelay.TotalMilliseconds * factor;

        // net48 没有 Math.Clamp（.NET Core 2.0+），手写等价的三段比较。
        var cap = MaximumDelay.TotalMilliseconds;
        var clamped = jittered < 0.0 ? 0.0
            : jittered > cap ? cap
            : jittered;

        return TimeSpan.FromMilliseconds(clamped);
    }

    /// <summary>
    /// 判断是否还允许再重试一次。
    /// </summary>
    /// <param name="consecutiveFailures">已连续失败的次数。</param>
    /// <returns>允许重试返回 <see langword="true"/>。</returns>
    public bool ShouldRetry(int consecutiveFailures)
    {
        Guard.ThrowIfNegative(consecutiveFailures);
        return MaxAttempts == 0 || consecutiveFailures < MaxAttempts;
    }

    /// <inheritdoc />
    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        $"ReconnectPolicy(初始 {InitialDelay.TotalMilliseconds:F0}ms × {Multiplier:F1}, 上限 {MaximumDelay.TotalMilliseconds:F0}ms, 抖动 ±{JitterFactor:P0}, 最大尝试 {(MaxAttempts == 0 ? "无限" : MaxAttempts.ToString(CultureInfo.InvariantCulture))})");
}