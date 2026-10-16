using System.Collections.Concurrent;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Configuration;
using StreamingAggregation.Infrastructure.OneBot;
using StreamingAggregation.Infrastructure.ViewModels;
using StreamingAggregation.Tests.Fixtures;

namespace StreamingAggregation.Tests.OneBot;

/// <summary>
/// 连接错误透传测试：真实失败原因必须一路活到界面文本。
/// </summary>
/// <remarks>
/// <para>
/// 这组用例盯的是一条具体的覆盖链。三条泛化文案依次经过——
///
/// <list type="bullet">
///   <item><description>「第 N 次连接尝试。」（每次重试的循环头）</description></item>
///   <item><description>「等待退避后重连。」（退避等待阶段）</description></item>
///   <item><description>「事件循环结束。」（循环收尾）</description></item>
/// </list>
///
/// 它们此前都能把真正的握手失败原因从状态栏里挤掉：事件源只把原因写进 <c>LastErrorMessage</c>，
/// 而真实错误只落在 <see cref="ConnectionStateTracker.LastError"/> 里，组合根又从不读它。
/// </para>
/// <para>
/// 每个用例都按组合根的接线方式把状态事件喂进 <see cref="ConnectionStatusViewModel"/>，
/// 因此断言的是"用户真正会看到的文本"，而不是某个中间字段。
/// </para>
/// </remarks>
public sealed class OneBotConnectionErrorPassthroughTests
{
    /// <summary>握手用的假令牌；仅存在于测试进程内，绝非真实凭据。</summary>
    private const string HandshakeToken = "test-token-error-passthrough";

    /// <summary>泄漏哨兵：一旦出现在任何出口即视为脱敏失效。</summary>
    private const string LeakCanary = "test-token-SHOULD-NOT-LEAK-yyy";

    /// <summary>让客户端在观察窗内密集重试，避免测试依赖默认的 1 秒退避。</summary>
    private static readonly ReconnectPolicy FastPolicy = new(
        initialDelay: TimeSpan.FromMilliseconds(10),
        maximumDelay: TimeSpan.FromMilliseconds(20),
        multiplier: 1.0,
        jitterFactor: 0.0);

    // ---------------------------------------------------------------------
    // 握手失败：真实原因必须到达界面文本
    // ---------------------------------------------------------------------

    [Fact]
    public async Task StreamAsync_WhenHandshakeFails_RealErrorReachesStatusText()
    {
        await using var server = new TestWebSocketServer(rejectWith401: true);
        await server.StartAsync();
        var diagnostics = new ConcurrentQueue<OneBotDiagnostic>();

        await using var client = CreateClient(server, diagnostics, () => HandshakeToken);
        var model = AttachStatusModel(client);

        var snapshot = await ObserveUntilAsync(client, () => server.ConnectionCount >= 3, TimeSpan.FromSeconds(10));

        // 前提成立：确实反复握手失败，且从未连上。
        Assert.Equal(0, snapshot.SuccessfulConnectionCount);
        Assert.True(server.ConnectionCount >= 3, $"应至少握手三次，实际 {server.ConnectionCount} 次。");

        var error = Assert.IsType<string>(model.LastErrorMessage);

        // 401 是握手状态码问题，用户必须能从状态栏直接看出来。
        Assert.True(
            error.Contains("401", StringComparison.Ordinal) ||
            error.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase),
            $"状态栏里的错误应指向握手状态码，实际为：{error}");

        // 状态栏长文本同样带着这条真实原因。
        Assert.Contains(error, model.DetailText, StringComparison.Ordinal);

        // 令牌绝不能随这条错误外泄。
        Assert.DoesNotContain(HandshakeToken, error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamAsync_WhenHandshakeFails_GenericRetryTextNeverOverwritesRealError()
    {
        await using var server = new TestWebSocketServer(rejectWith401: true);
        await server.StartAsync();
        var diagnostics = new ConcurrentQueue<OneBotDiagnostic>();
        var transitions = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        var lastErrors = new ConcurrentQueue<string?>();

        await using var client = CreateClient(server, diagnostics, () => HandshakeToken);
        var model = new ConnectionStatusViewModel();
        var tracker = client.Tracker;

        client.StateChanged += (_, e) => transitions.Enqueue(e);
        client.StateChanged += (_, e) => model.Apply(e.Current, e.Reason, tracker.LastError);
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConnectionStatusViewModel.LastErrorMessage))
            {
                lastErrors.Enqueue(model.LastErrorMessage);
            }
        };

        await ObserveUntilAsync(client, () => server.ConnectionCount >= 5, TimeSpan.FromSeconds(10));

        // 前提：这轮里确实推过泛化的重试文案，否则下面的断言会因"压根没走到那里"而空过。
        Assert.Contains(
            transitions,
            e => e.Reason is not null &&
                 (e.Reason.Contains("次连接尝试", StringComparison.Ordinal) ||
                  e.Reason.Contains("等待退避后重连", StringComparison.Ordinal)));

        // 每一次观察到非空 LastErrorMessage 的时刻，它都必须仍然是那条真实错误。
        var observed = lastErrors.Where(v => v is not null).ToArray();
        Assert.NotEmpty(observed);
        Assert.All(
            observed,
            value =>
            {
                Assert.DoesNotContain("次连接尝试", value, StringComparison.Ordinal);
                Assert.DoesNotContain("等待退避后重连", value, StringComparison.Ordinal);
                Assert.DoesNotContain("事件循环结束", value, StringComparison.Ordinal);
                Assert.DoesNotContain(HandshakeToken, value, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task StreamAsync_WhenHandshakeFails_RealErrorIsPushedThroughStateEvents()
    {
        await using var server = new TestWebSocketServer(rejectWith401: true);
        await server.StartAsync();
        var diagnostics = new ConcurrentQueue<OneBotDiagnostic>();
        var transitions = new ConcurrentQueue<ConnectionStateChangedEventArgs>();

        await using var client = CreateClient(server, diagnostics, () => HandshakeToken);
        client.StateChanged += (_, e) => transitions.Enqueue(e);

        await ObserveUntilAsync(client, () => server.ConnectionCount >= 3, TimeSpan.FromSeconds(10));

        // 事件流里必须出现带着真实错误的那次迁移。只 RecordError 是不够的：
        // 组合根只订阅 StateChanged，错误不落到事件里界面就永远收不到。
        Assert.Contains(
            transitions,
            e => e.Current == ConnectionState.Reconnecting &&
                 e.Reason is not null &&
                 (e.Reason.Contains("401", StringComparison.Ordinal) ||
                  e.Reason.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase)));
    }

    // ---------------------------------------------------------------------
    // 令牌读取失败：真实原因同样要活下来，且继续 fail-closed
    // ---------------------------------------------------------------------

    [Fact]
    public async Task StreamAsync_WhenTokenProviderThrows_RealErrorReachesStatusText()
    {
        await using var server = new TestWebSocketServer();
        await server.StartAsync();
        var diagnostics = new ConcurrentQueue<OneBotDiagnostic>();

        await using var client = CreateClient(server, diagnostics, ThrowingTokenProvider);
        var model = AttachStatusModel(client);

        var snapshot = await ObserveForAsync(client, TimeSpan.FromMilliseconds(600));

        var error = Assert.IsType<string>(model.LastErrorMessage);
        Assert.Contains("访问令牌", error, StringComparison.Ordinal);
        Assert.Contains(error, model.DetailText, StringComparison.Ordinal);

        // fail-closed 没有因为错误透传而松动：一次 TCP 连接都不该发生。
        Assert.Equal(0, server.ConnectionCount);
        Assert.Equal(0, snapshot.SuccessfulConnectionCount);
    }

    // ---------------------------------------------------------------------
    // 脱敏：错误透传不能把令牌带出去
    // ---------------------------------------------------------------------

    [Fact]
    public async Task StreamAsync_WhenHandshakeFails_StatusTextNeverLeaksToken()
    {
        await using var server = new TestWebSocketServer(rejectWith401: true);
        await server.StartAsync();
        var diagnostics = new ConcurrentQueue<OneBotDiagnostic>();
        var transitions = new ConcurrentQueue<ConnectionStateChangedEventArgs>();

        await using var client = CreateClient(server, diagnostics, () => LeakCanary);
        var model = AttachStatusModel(client);
        client.StateChanged += (_, e) => transitions.Enqueue(e);

        await ObserveUntilAsync(client, () => server.ConnectionCount >= 3, TimeSpan.FromSeconds(10));

        var outlets = new List<(string Label, string? Text)>
        {
            ("LastErrorMessage", model.LastErrorMessage),
            ("StatusReason", model.StatusReason),
            ("StatusDescription", model.StatusDescription),
            ("DetailText", model.DetailText),
            ("client.LastErrorMessage", client.LastErrorMessage),
        };

        foreach (var transition in transitions)
        {
            outlets.Add(($"state-change.Reason[{transition.Current}]", transition.Reason));
        }

        foreach (var diagnostic in diagnostics)
        {
            outlets.Add(($"diagnostic.Message[{diagnostic.Level}]", diagnostic.Message));
        }

        Assert.All(
            outlets,
            o => Assert.False(
                o.Text?.Contains(LeakCanary, StringComparison.Ordinal) ?? false,
                $"令牌泄漏到出口「{o.Label}」：{o.Text}"));
    }

    // ---------------------------------------------------------------------
    // 测试脚手架
    // ---------------------------------------------------------------------

    /// <summary>令牌委托抛异常，模拟密钥库读不到令牌。</summary>
    private static string? ThrowingTokenProvider() =>
        throw new InvalidOperationException("DPAPI 解密失败。");

    private static OneBotWebSocketClient CreateClient(
        TestWebSocketServer server,
        ConcurrentQueue<OneBotDiagnostic> diagnostics,
        Func<string?> tokenProvider) =>
        new(
            new OneBotConnectionSettings { Host = "127.0.0.1", Port = server.Port, UseSsl = false },
            tokenProvider,
            FastPolicy,
            diagnostics: diagnostics.Enqueue);

    /// <summary>
    /// 按组合根的接线方式把状态事件推进连接状态绑定：状态、原因、以及 tracker 里的真实错误。
    /// </summary>
    private static ConnectionStatusViewModel AttachStatusModel(OneBotWebSocketClient client)
    {
        var model = new ConnectionStatusViewModel();
        var tracker = client.Tracker;
        client.StateChanged += (_, e) => model.Apply(e.Current, e.Reason, tracker.LastError);
        return model;
    }

    /// <summary>固定观察窗后取样。取样发生在取消之前，否则读到的永远是收尾状态。</summary>
    private static async Task<ConnectionStateSnapshot> ObserveForAsync(OneBotWebSocketClient client, TimeSpan window)
    {
        using var cts = new CancellationTokenSource();
        var consume = ConsumeAsync(client, cts.Token);

        await Task.Delay(window);
        var snapshot = client.Tracker.Snapshot();

        await cts.CancelAsync();
        await consume;
        return snapshot;
    }

    /// <summary>持续驱动事件流直到条件成立或超时；同样在取消前取样。</summary>
    private static async Task<ConnectionStateSnapshot> ObserveUntilAsync(
        OneBotWebSocketClient client,
        Func<bool> condition,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var consume = ConsumeAsync(client, cts.Token);

        while (!cts.IsCancellationRequested && !condition())
        {
            await Task.Delay(20);
        }

        var snapshot = client.Tracker.Snapshot();
        await cts.CancelAsync();
        await consume;
        return snapshot;
    }

    /// <summary>消费事件流直到取消；取消与通道关闭都算正常结束。</summary>
    private static async Task ConsumeAsync(OneBotWebSocketClient client, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var _ in client.StreamAsync(cancellationToken))
            {
                // 这些用例里不会有事件真的到达；消费掉即可。
            }
        }
        catch (OperationCanceledException)
        {
            // 取消是预期的结束方式。
        }
    }
}