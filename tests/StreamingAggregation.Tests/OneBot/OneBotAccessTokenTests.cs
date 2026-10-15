using System.Collections.Concurrent;
using System.Security.Cryptography;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Configuration;
using StreamingAggregation.Infrastructure.OneBot;
using StreamingAggregation.Tests.Fixtures;

namespace StreamingAggregation.Tests.OneBot;

/// <summary>
/// 令牌读取与握手行为的行为测试。
/// </summary>
/// <remarks>
/// <para>关键区分：</para>
/// <list type="bullet">
///   <item><description>令牌委托返回 <see langword="null"/> / 空串 = 「本来就没有令牌」，必须照常发起匿名连接（本地 SnowLuma 可以不配 access token）。</description></item>
///   <item><description>令牌委托抛异常 = 「不知道令牌」，必须中止连接，绝不退化为匿名连接。</description></item>
/// </list>
/// <para>
/// 握手本身交给 <see cref="TestWebSocketServer"/> 完成真实的 RFC 6455 握手，
/// 因此可以直接断言服务器<strong>收到的那一次请求</strong>里有没有 <c>Authorization</c> 头，
/// 而不是只统计「有没有发起过 TCP 连接」——后者无法区分「带了错的头」与「根本没带」。
/// </para>
/// <para>
/// 全部用例只使用回环地址上的临时测试服务器与假令牌，不触碰真实 SnowLuma 或 <c>%LOCALAPPDATA%</c>。
/// </para>
/// </remarks>
public sealed class OneBotAccessTokenTests
{
    /// <summary>握手断言用的假令牌；仅存在于测试进程内，绝非真实凭据。</summary>
    private const string HandshakeToken = "test-token-abc";

    /// <summary>令牌委托内部异常文案里出现的假令牌，用于验证 fail-closed 路径的脱敏。</summary>
    private const string SecretToken = "super-secret-onebot-token";

    /// <summary>泄漏哨兵：可被一眼识别的假令牌，一旦出现在任何出口即视为脱敏失效。</summary>
    private const string LeakCanary = "test-token-SHOULD-NOT-LEAK-xxx";

    /// <summary>让客户端在观察窗内密集重试，避免测试依赖默认的 1 秒退避。</summary>
    private static readonly ReconnectPolicy FastPolicy = new(
        initialDelay: TimeSpan.FromMilliseconds(10),
        maximumDelay: TimeSpan.FromMilliseconds(20),
        multiplier: 1.0,
        jitterFactor: 0.0);

    /// <summary>长退避：握手失败后会稳定停在「等待退避」阶段，便于断言 <see cref="ConnectionState.Reconnecting"/>。</summary>
    private static readonly ReconnectPolicy SlowPolicy = new(
        initialDelay: TimeSpan.FromSeconds(2),
        maximumDelay: TimeSpan.FromSeconds(2),
        multiplier: 1.0,
        jitterFactor: 0.0);

    // ---------------------------------------------------------------------
    // fail-closed：令牌读不到时，一次 TCP 连接都不该发生
    // ---------------------------------------------------------------------

    [Fact]
    public async Task StreamAsync_WhenTokenProviderThrows_OpensNoTcpConnection()
    {
        await using var server = new TestWebSocketServer();
        await server.StartAsync();
        var diagnostics = new ConcurrentQueue<OneBotDiagnostic>();

        await using var client = CreateClient(server, diagnostics, ThrowingTokenProvider);

        await ObserveForAsync(client, TimeSpan.FromMilliseconds(600));

        // fail-closed 的核心断言：一次 TCP 连接都不该发生，更不会有握手。
        Assert.Equal(0, server.ConnectionCount);
        Assert.Empty(server.CapturedHeaders);
    }

    [Fact]
    public async Task StreamAsync_WhenTokenProviderThrows_ReportsSanitizedReasonToTracker()
    {
        await using var server = new TestWebSocketServer();
        await server.StartAsync();
        var diagnostics = new ConcurrentQueue<OneBotDiagnostic>();

        await using var client = CreateClient(server, diagnostics, ThrowingTokenProvider);

        await ObserveForAsync(client, TimeSpan.FromMilliseconds(600));

        var lastError = Assert.IsType<string>(client.LastErrorMessage);
        Assert.Contains("访问令牌", lastError, StringComparison.Ordinal);

        // 原始异常的文案里带着令牌，绝不能顺着脱敏链路漏到状态或日志里。
        Assert.DoesNotContain(SecretToken, lastError, StringComparison.Ordinal);
        Assert.All(
            diagnostics,
            d => Assert.DoesNotContain(SecretToken, d.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task StreamAsync_WhenTokenProviderThrows_RaisesTypedExceptionWithFixedMessage()
    {
        await using var server = new TestWebSocketServer();
        await server.StartAsync();
        var diagnostics = new ConcurrentQueue<OneBotDiagnostic>();

        await using var client = CreateClient(server, diagnostics, ThrowingTokenProvider);

        await ObserveForAsync(client, TimeSpan.FromMilliseconds(600));

        var raised = diagnostics
            .Where(d => d.Exception is OneBotAccessTokenException)
            .Select(d => (OneBotAccessTokenException)d.Exception!)
            .ToArray();

        Assert.NotEmpty(raised);

        // 排障线索保留在类型名里：既知道是哪种失败，又不会带出原始文案。
        Assert.All(raised, e => Assert.StartsWith(OneBotAccessTokenException.Reason, e.Message, StringComparison.Ordinal));
        Assert.All(raised, e => Assert.Equal(typeof(InvalidOperationException).FullName, e.SourceType));

        // ToString() 是异常最容易被顺手落地的一处出口，必须同样不出现令牌。
        Assert.All(raised, e => Assert.DoesNotContain(SecretToken, e.ToString()!, StringComparison.Ordinal));
        Assert.All(raised, e => Assert.Null(e.InnerException));

        // 按 Error 上报，映射层不能把它降级成 Debug。
        Assert.All(
            diagnostics.Where(d => d.Exception is OneBotAccessTokenException),
            d => Assert.Equal(OneBotDiagnosticLevel.Error, d.Level));
    }

    [Fact]
    public async Task StreamAsync_WhenTokenProviderThrows_PushesReasonToConnectionState()
    {
        await using var server = new TestWebSocketServer();
        await server.StartAsync();
        var transitions = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        var diagnostics = new ConcurrentQueue<OneBotDiagnostic>();

        await using var client = CreateClient(server, diagnostics, ThrowingTokenProvider);
        client.StateChanged += (_, e) => transitions.Enqueue(e);

        await ObserveForAsync(client, TimeSpan.FromMilliseconds(600));

        Assert.Contains(
            transitions,
            e => e.Reason is not null && e.Reason.Contains("访问令牌", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------------
    // 握手带令牌：Authorization 头必须真的出现在服务器收到的那次握手里
    // ---------------------------------------------------------------------

    [Fact]
    public async Task StreamAsync_WhenTokenProvided_ConnectsWithBearerAuthorizationHeader()
    {
        await using var server = new TestWebSocketServer();
        await server.StartAsync();
        var diagnostics = new ConcurrentQueue<OneBotDiagnostic>();

        await using var client = CreateClient(server, diagnostics, () => HandshakeToken);

        var snapshot = await ObserveUntilAsync(
            client,
            () => client.Tracker.SuccessfulConnectionCount > 0,
            TimeSpan.FromSeconds(10));

        Assert.Equal(ConnectionState.Connected, snapshot.State);
        Assert.True(snapshot.SuccessfulConnectionCount > 0);

        var headers = Assert.Single(server.CapturedHeaders);
        Assert.True(headers.TryGetValue("Authorization", out var authorization));
        Assert.Equal($"Bearer {HandshakeToken}", authorization);
        Assert.DoesNotContain(diagnostics, d => d.Level == OneBotDiagnosticLevel.Error);
    }

    [Fact]
    public async Task StreamAsync_WhenTokenRotates_RereadsItOnEveryReconnectAttempt()
    {
        // 401 让每次握手都失败，于是每次尝试都会重新读取令牌——正好用来观察轮换。
        await using var server = new TestWebSocketServer(rejectWith401: true);
        await server.StartAsync();
        var diagnostics = new ConcurrentQueue<OneBotDiagnostic>();

        var calls = 0;
        await using var client = CreateClient(
            server,
            diagnostics,
            () => Interlocked.Increment(ref calls) == 1 ? "test-token-first" : "test-token-rotated");

        var snapshot = await ObserveUntilAsync(client, () => server.ConnectionCount >= 3, TimeSpan.FromSeconds(10));

        Assert.Equal(0, snapshot.SuccessfulConnectionCount);

        var authorizationValues = server.CapturedHeaders
            .Select(h => h.TryGetValue("Authorization", out var value) ? value : null)
            .ToArray();

        // 令牌在每次重连时重新获取，因此轮换自动生效，无需重启客户端。
        Assert.Contains("Bearer test-token-first", authorizationValues);
        Assert.Contains("Bearer test-token-rotated", authorizationValues);

        // 每一次连接都带上了 Authorization，没有漏发的匿名重连。
        Assert.DoesNotContain(authorizationValues, v => v is null);
    }

    // ---------------------------------------------------------------------
    // 匿名连接：本地 SnowLuma 不配 token 时必须照常连上（最关键的一组）
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task StreamAsync_WhenTokenAbsent_ConnectsWithoutAuthorizationHeader(string? token)
    {
        await using var server = new TestWebSocketServer();
        await server.StartAsync();
        var diagnostics = new ConcurrentQueue<OneBotDiagnostic>();

        await using var client = CreateClient(server, diagnostics, () => token);

        var snapshot = await ObserveUntilAsync(
            client,
            () => client.Tracker.SuccessfulConnectionCount > 0,
            TimeSpan.FromSeconds(10));

        // 空令牌是一等合法状态：握手必须真的完成，而不是被 fail-closed 误伤。
        Assert.Equal(ConnectionState.Connected, snapshot.State);
        Assert.True(snapshot.SuccessfulConnectionCount > 0);
        Assert.Null(client.LastErrorMessage);

        Assert.NotEmpty(server.CapturedHeaders);
        Assert.All(server.CapturedHeaders, h => Assert.False(h.ContainsKey("Authorization")));

        // 字段名本身没写错：由 StreamAsync_WhenTokenProvided_ConnectsWithBearerAuthorizationHeader
        // 证明同一台服务器确实能收到 Authorization 头，两条用例合起来才排除了「断言写错字段」。
        // 这里只负责确认「不配令牌」没有被 fail-closed 误伤，也没有产生任何错误诊断。
        Assert.DoesNotContain(diagnostics, d => d.Exception is OneBotAccessTokenException);
        Assert.DoesNotContain(diagnostics, d => d.Level == OneBotDiagnosticLevel.Error);
    }

    // ---------------------------------------------------------------------
    // 401：进入退避重连，不能静默成功，也不能被误报成「令牌读取失败」
    // ---------------------------------------------------------------------

    [Fact]
    public async Task StreamAsync_WhenServerReturns401_PublishesReconnectingState()
    {
        await using var server = new TestWebSocketServer(rejectWith401: true);
        await server.StartAsync();
        var diagnostics = new ConcurrentQueue<OneBotDiagnostic>();

        // 长退避保证失败后停在「等待退避」阶段，状态采样是确定的。
        await using var client = CreateClient(server, diagnostics, SlowPolicy, () => HandshakeToken);

        var snapshot = await ObserveForAsync(client, TimeSpan.FromMilliseconds(600));

        Assert.Equal(ConnectionState.Reconnecting, snapshot.State);
        Assert.Equal(0, snapshot.SuccessfulConnectionCount);
        Assert.True(server.ConnectionCount > 0);
    }

    [Fact]
    public async Task StreamAsync_WhenServerReturns401_KeepsRetryingAndNeverReportsSuccess()
    {
        await using var server = new TestWebSocketServer(rejectWith401: true);
        await server.StartAsync();
        var transitions = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        var diagnostics = new ConcurrentQueue<OneBotDiagnostic>();

        await using var client = CreateClient(server, diagnostics, () => HandshakeToken);
        client.StateChanged += (_, e) => transitions.Enqueue(e);

        var snapshot = await ObserveForAsync(client, TimeSpan.FromMilliseconds(600));

        // 握手被拒绝时绝不能出现 Connected——静默成功比失败更难排查。
        Assert.DoesNotContain(transitions, e => e.Current == ConnectionState.Connected);
        Assert.Equal(0, snapshot.SuccessfulConnectionCount);

        // 进入退避并持续重试：服务端确实被反复敲，而不是试一次就放弃。
        Assert.Contains(transitions, e => e.Current == ConnectionState.Reconnecting);
        Assert.True(snapshot.ReconnectCount >= 2, $"应至少重试一次，实际重连 {snapshot.ReconnectCount} 次。");
        Assert.True(server.ConnectionCount >= 2, $"服务端应收到至少两次握手，实际 {server.ConnectionCount} 次。");

        // 401 是服务端拒绝，不是令牌读不到——两者混淆会让用户去查错的地方。
        Assert.DoesNotContain(diagnostics, d => d.Exception is OneBotAccessTokenException);
    }

    [Fact]
    public async Task StreamAsync_WhenServerReturns401_SurfacesHandshakeFailureInLastError()
    {
        await using var server = new TestWebSocketServer(rejectWith401: true);
        await server.StartAsync();
        var diagnostics = new ConcurrentQueue<OneBotDiagnostic>();

        await using var client = CreateClient(server, diagnostics, () => HandshakeToken);

        await ObserveForAsync(client, TimeSpan.FromMilliseconds(600));

        var lastError = Assert.IsType<string>(client.LastErrorMessage);

        // 至少要能看出是握手状态码问题（401 / Unauthorized）。
        Assert.True(
            lastError.Contains("401", StringComparison.Ordinal) ||
            lastError.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase),
            $"401 握手的错误摘要应指向状态码或未授权，实际为：{lastError}");

        // 令牌本身仍不得随错误摘要外泄。
        Assert.DoesNotContain(HandshakeToken, lastError, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------------
    // 脱敏：令牌值不得出现在任何出口
    // ---------------------------------------------------------------------

    [Fact]
    public async Task StreamAsync_WhenTokenProvided_NeverExposesTokenOnAnyOutlet()
    {
        // 用一个会失败的握手，逼出所有「错误信息 + 异常对象」出口，而不是只测成功路径。
        await using var server = new TestWebSocketServer(rejectWith401: true);
        await server.StartAsync();
        var transitions = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        var diagnostics = new ConcurrentQueue<OneBotDiagnostic>();

        await using var client = CreateClient(server, diagnostics, () => LeakCanary);
        client.StateChanged += (_, e) => transitions.Enqueue(e);

        var snapshot = await ObserveUntilAsync(client, () => server.ConnectionCount >= 2, TimeSpan.FromSeconds(10));

        // 先确认前提成立：确实连过、确实带了这个令牌、确实失败过。
        Assert.Equal(0, snapshot.SuccessfulConnectionCount);
        Assert.Contains(
            server.CapturedHeaders,
            h => h.TryGetValue("Authorization", out var value) && value == $"Bearer {LeakCanary}");

        AssertNoTokenLeak(client, diagnostics, transitions, LeakCanary);
    }

    [Fact]
    public async Task StreamAsync_WhenTokenProviderThrowsWithTokenInMessage_LeaksNothing()
    {
        await using var server = new TestWebSocketServer();
        await server.StartAsync();
        var transitions = new ConcurrentQueue<ConnectionStateChangedEventArgs>();
        var diagnostics = new ConcurrentQueue<OneBotDiagnostic>();

        // 令牌以明文出现在原始异常里——最容易漏的一处。
        await using var client = CreateClient(server, diagnostics, ThrowingLeakyTokenProvider);
        client.StateChanged += (_, e) => transitions.Enqueue(e);

        await ObserveForAsync(client, TimeSpan.FromMilliseconds(600));

        var raised = diagnostics
            .Where(d => d.Exception is OneBotAccessTokenException)
            .Select(d => (OneBotAccessTokenException)d.Exception!)
            .ToArray();

        Assert.NotEmpty(raised);
        Assert.All(raised, e => Assert.Null(e.InnerException));

        AssertNoTokenLeak(client, diagnostics, transitions, LeakCanary);
    }

    // ---------------------------------------------------------------------
    // 测试脚手架
    // ---------------------------------------------------------------------

    /// <summary>令牌委托内部含令牌文案，用来验证脱敏链路。</summary>
    private static string? ThrowingTokenProvider() =>
        throw new InvalidOperationException($"DPAPI 解密失败，明文为 {SecretToken}");

    /// <summary>令牌委托抛出的异常里直接写着明文令牌。</summary>
    private static string? ThrowingLeakyTokenProvider() =>
        throw new CryptographicException($"读取访问令牌失败，明文令牌：{LeakCanary}");

    private static OneBotWebSocketClient CreateClient(
        TestWebSocketServer server,
        ConcurrentQueue<OneBotDiagnostic> diagnostics,
        Func<string?> tokenProvider) =>
        CreateClient(server, diagnostics, FastPolicy, tokenProvider);

    private static OneBotWebSocketClient CreateClient(
        TestWebSocketServer server,
        ConcurrentQueue<OneBotDiagnostic> diagnostics,
        ReconnectPolicy policy,
        Func<string?> tokenProvider) =>
        new(
            new OneBotConnectionSettings { Host = "127.0.0.1", Port = server.Port, UseSsl = false },
            tokenProvider,
            policy,
            diagnostics: diagnostics.Enqueue);

    /// <summary>
    /// 固定观察窗后取样。取样发生在取消<strong>之前</strong>，
    /// 否则读到的永远是收尾状态 <see cref="ConnectionState.Disconnected"/>。
    /// </summary>
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

    /// <summary>
    /// 遍历令牌可能外泄的全部出口：诊断消息、异常消息/ToString/InnerException 链、
    /// 最近错误、状态迁移原因以及各个 <c>ToString()</c>。
    /// </summary>
    private static void AssertNoTokenLeak(
        OneBotWebSocketClient client,
        IEnumerable<OneBotDiagnostic> diagnostics,
        IEnumerable<ConnectionStateChangedEventArgs> transitions,
        string canary)
    {
        var outlets = new List<(string Label, string? Text)>
        {
            ("LastErrorMessage", client.LastErrorMessage),
            ("client.ToString()", client.ToString()),
            ("client.Endpoint", client.Endpoint),
            ("tracker.ToString()", client.Tracker.ToString()),
            ("tracker.Snapshot().ToString()", client.Tracker.Snapshot().ToString()),
        };

        foreach (var diagnostic in diagnostics)
        {
            outlets.Add(($"diagnostic.Message[{diagnostic.Level}]", diagnostic.Message));

            for (var exception = diagnostic.Exception; exception is not null; exception = exception.InnerException)
            {
                var label = exception.GetType().Name;
                outlets.Add(($"diagnostic.Exception[{label}].Message", exception.Message));
                outlets.Add(($"diagnostic.Exception[{label}].ToString()", exception.ToString()));
            }
        }

        foreach (var transition in transitions)
        {
            outlets.Add(($"state-change.Reason[{transition.Current}]", transition.Reason));
            outlets.Add(("state-change.ToString()", transition.ToString()));
        }

        Assert.All(
            outlets,
            o => Assert.False(
                o.Text?.Contains(canary, StringComparison.Ordinal) ?? false,
                $"令牌泄漏到出口「{o.Label}」：{o.Text}"));
    }
}
