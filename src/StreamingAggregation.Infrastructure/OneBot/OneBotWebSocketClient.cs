using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Configuration;
using StreamingAggregation.Core.Models;

namespace StreamingAggregation.Infrastructure.OneBot;

/// <summary>诊断日志级别。</summary>
public enum OneBotDiagnosticLevel
{
    /// <summary>调试信息。</summary>
    Debug,

    /// <summary>常规信息。</summary>
    Info,

    /// <summary>警告。</summary>
    Warning,

    /// <summary>错误。</summary>
    Error,
}

/// <summary>
/// 一条脱敏后的诊断记录。
/// </summary>
/// <param name="Level">日志级别。</param>
/// <param name="Message">日志正文；已确保不包含访问令牌。</param>
/// <param name="Exception">关联异常，可为 <see langword="null"/>。</param>
public sealed record OneBotDiagnostic(OneBotDiagnosticLevel Level, string Message, Exception? Exception = null);

/// <summary>
/// OneBot 正向 WebSocket 事件源客户端。
/// </summary>
/// <remarks>
/// <para>行为约定：</para>
/// <list type="number">
///   <item><description>使用 <see cref="ClientWebSocket"/> 连接 <c>ws://127.0.0.1:3001/</c>（或 wss），握手头携带 <c>Authorization: Bearer &lt;token&gt;</c>。</description></item>
///   <item><description>访问令牌通过委托在<strong>每次重连时</strong>重新获取，因此令牌轮换自动生效；令牌只出现在握手头中，绝不写入日志或 <see cref="ToString"/>。</description></item>
///   <item><description>按 <see cref="WebSocketReceiveResult.EndOfMessage"/> 循环拼包，完整还原一条 JSON 帧后才解析，解决 WebSocket 分包问题。</description></item>
///   <item><description>断线后按 <see cref="ReconnectPolicy"/>（指数退避 + 抖动 + 上限）自动重连，<see cref="CancellationToken"/> 全链路贯穿。</description></item>
///   <item><description><strong>只读</strong>：仅接收事件，不发送任何 OneBot API 请求，不写 SnowLuma 数据。</description></item>
///   <item><description>非回环地址强制使用 WSS：明文 <c>ws://</c> 仅允许回环主机，构造远程明文端点时抛 <see cref="ArgumentException"/>。</description></item>
/// </list>
/// <para>
/// 事件流由一条后台循环 + 无界通道驱动；<see cref="StreamAsync"/> 收到的令牌同时管辖该循环，
/// 因此同一时刻应只有一个消费者。循环正常结束时流正常结束；不可恢复错误通过通道传递给消费者。
/// </para>
/// </remarks>
public sealed class OneBotWebSocketClient : IOneBotEventSource, IDisposable, IAsyncDisposable
{
    /// <summary>访问令牌在 <see cref="ISecretStore"/> 中的默认键名。</summary>
    public const string DefaultSecretKey = "onebot.access_token";

    private const int ReceiveChunkSize = 8 * 1024;
    private const int DefaultMaxMessageBytes = 4 * 1024 * 1024;
    private const int KeepAliveIntervalSeconds = 30;

    private readonly Func<string?> _accessTokenProvider;
    private readonly ReconnectPolicy _reconnectPolicy;
    private readonly ConnectionStateTracker _tracker = new();
    private readonly int _maxMessageBytes;
    private readonly Uri _endpoint;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();

    private ClientWebSocket? _activeSocket;
    private string? _activeToken;
    private RunHandle? _run;
    private bool _disposed;

    /// <summary>
    /// 使用委托提供访问令牌创建客户端。
    /// </summary>
    /// <param name="settings">连接配置（主机、端口、是否 TLS）。令牌不从这里读取。</param>
    /// <param name="accessTokenProvider">
    /// 访问令牌提供委托，返回 <see langword="null"/> 或空串表示不带 <c>Authorization</c> 头。
    /// 每次连接尝试都会调用一次。
    /// </param>
    /// <param name="reconnectPolicy">重连退避策略，默认 <see cref="ReconnectPolicy.Default"/>。</param>
    /// <param name="maxMessageBytes">单帧最大字节数，超出视为协议异常，默认 4 MiB。</param>
    /// <param name="diagnostics">可选诊断回调，收到的是已脱敏的消息。</param>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> 或 <paramref name="accessTokenProvider"/> 为 <see langword="null"/>。</exception>
    /// <exception cref="ArgumentOutOfRangeException">主机为空或端口越界。</exception>
    /// <exception cref="ArgumentException">明文 WebSocket (<c>ws://</c>) 仅允许回环地址；远程主机必须使用 WSS。</exception>
    public OneBotWebSocketClient(
        OneBotConnectionSettings settings,
        Func<string?> accessTokenProvider,
        ReconnectPolicy? reconnectPolicy = null,
        int maxMessageBytes = DefaultMaxMessageBytes,
        Action<OneBotDiagnostic>? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(accessTokenProvider);

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxMessageBytes, 0, "单帧上限必须大于零。");

        _endpoint = BuildEndpoint(settings.Host, settings.Port, settings.UseSsl);
        _accessTokenProvider = accessTokenProvider;
        _reconnectPolicy = reconnectPolicy ?? ReconnectPolicy.Default;
        _maxMessageBytes = maxMessageBytes;
        Diagnostics = diagnostics;

        Diagnostics?.Invoke(new OneBotDiagnostic(OneBotDiagnosticLevel.Info, $"OneBot 事件源已创建，端点 {_endpoint}。"));
    }

    /// <summary>
    /// 使用密钥存储（T8 的 DPAPI 实现）提供访问令牌创建客户端。
    /// </summary>
    /// <param name="settings">连接配置。</param>
    /// <param name="secretStore">密钥存储。</param>
    /// <param name="secretKey">令牌键名，默认 <see cref="DefaultSecretKey"/>。</param>
    /// <param name="reconnectPolicy">重连退避策略。</param>
    /// <param name="maxMessageBytes">单帧最大字节数。</param>
    /// <param name="diagnostics">可选诊断回调。</param>
    public OneBotWebSocketClient(
        OneBotConnectionSettings settings,
        ISecretStore secretStore,
        string secretKey = DefaultSecretKey,
        ReconnectPolicy? reconnectPolicy = null,
        int maxMessageBytes = DefaultMaxMessageBytes,
        Action<OneBotDiagnostic>? diagnostics = null)
        : this(
            settings,
            CreateSecretProvider(secretStore, secretKey),
            reconnectPolicy,
            maxMessageBytes,
            diagnostics)
    {
    }

    /// <summary>当前连接状态。</summary>
    public ConnectionState State => _tracker.Current;

    /// <summary>连接状态跟踪器，含统计与最近错误。</summary>
    public ConnectionStateTracker Tracker => _tracker;

    /// <summary>实际连接的 WebSocket 端点（不含任何凭据）。</summary>
    public string Endpoint => _endpoint.ToString();

    /// <summary>最近一次错误摘要（已脱敏）。</summary>
    public string? LastErrorMessage => _tracker.LastError;

    /// <summary>连接状态变更事件。</summary>
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged
    {
        add => _tracker.StateChanged += value;
        remove => _tracker.StateChanged -= value;
    }

    /// <summary>可选的诊断回调。</summary>
    private Action<OneBotDiagnostic>? Diagnostics { get; }

    /// <summary>
    /// 订阅群消息事件流。连接、断线重连与取消均由本方法内部管理。
    /// </summary>
    /// <param name="cancellationToken">
    /// 取消令牌，贯穿握手、接收、重连退避全过程；取消后流正常结束，不抛异常。
    /// </param>
    /// <returns>群消息事件异步序列。</returns>
    public async IAsyncEnumerable<GroupMessageEvent> StreamAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

        var handle = EnsureRunStarted(cancellationToken);
        var reader = handle.Channel.Reader;

        while (true)
        {
            GroupMessageEvent item;

            try
            {
                item = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                // 后台循环正常收尾：已无更多事件。
                yield break;
            }

            yield return item;
        }
    }

    /// <summary>
    /// 同步释放：取消运行循环并关闭底层套接字（不等待）。
    /// </summary>
    public void Dispose()
    {
        if (!MarkDisposed())
        {
            return;
        }

        StopCore();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 异步释放：取消运行循环、关闭套接字并等待后台循环退出。
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!MarkDisposed())
        {
            return;
        }

        StopCore();

        var handle = Volatile.Read(ref _run);
        if (handle is not null)
        {
            try
            {
                await handle.Task.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Diagnostics?.Invoke(new OneBotDiagnostic(OneBotDiagnosticLevel.Debug, "关闭期间后台循环以异常结束（已忽略）。", ex));
            }

            handle.Cancellation.Dispose();
        }

        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public override string ToString() => $"OneBotWebSocketClient(端点 {_endpoint}, 状态 {State}, {_reconnectPolicy})";

    private static Func<string?> CreateSecretProvider(ISecretStore secretStore, string secretKey)
    {
        ArgumentNullException.ThrowIfNull(secretStore);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretKey);

        return () => secretStore.GetSecret(secretKey);
    }

    private static Uri BuildEndpoint(string host, int port, bool useSsl)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentOutOfRangeException(nameof(host), host, "OneBot 主机地址不能为空。");
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "OneBot 端口必须位于 1-65535 之间。");
        }

        // 非回环地址强制使用 WSS：明文 WS 只允许回环端点，远程主机必须 TLS。
        if (!useSsl && !EndpointTransportPolicy.IsLoopback(host))
        {
            throw new ArgumentException(
                "OneBot 端点使用明文 WebSocket (ws://) 时主机必须是回环地址；远程主机请使用 WSS (wss://)。",
                nameof(host));
        }

        // UriBuilder 归一化默认端口：ws 默认 80、wss 默认 443，显式端口一律保留。
        return new UriBuilder(useSsl ? Uri.UriSchemeWss : Uri.UriSchemeWs, host, port, "/").Uri;
    }

    private bool MarkDisposed()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _disposed = true;
            return true;
        }
    }

    private void StopCore()
    {
        try
        {
            _shutdown.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略。
        }

        CloseActiveSocket();
        _tracker.TryTransition(ConnectionState.Disconnected, "客户端已停止。");
    }

    private void CloseActiveSocket()
    {
        var socket = Interlocked.Exchange(ref _activeSocket, null);
        if (socket is null)
        {
            return;
        }

        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                socket.Abort();
            }

            socket.Dispose();
        }
        catch (Exception ex)
        {
            Diagnostics?.Invoke(new OneBotDiagnostic(OneBotDiagnosticLevel.Debug, "关闭套接字时出现异常（已忽略）。", ex));
        }
    }

    private RunHandle EnsureRunStarted(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // 上一轮循环已结束（正常收尾或重试耗尽）时允许重新订阅。
            if (_run is null || _run.Task.IsCompleted)
            {
                var channel = Channel.CreateUnbounded<GroupMessageEvent>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false,
                });

                // 消费者令牌 + 客户端生命周期令牌共同管辖后台循环：取消枚举即停止连接。
                var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token, cancellationToken);
                var task = Task.Run(() => RunAsync(channel.Writer, cts.Token), CancellationToken.None);

                _run = new RunHandle(channel, cts, task);
                Diagnostics?.Invoke(new OneBotDiagnostic(OneBotDiagnosticLevel.Debug, "已启动 OneBot 事件循环。"));
            }

            return _run;
        }
    }

    /// <summary>
    /// 后台事件循环：连接 → 接收 → 断线退避重连，直到取消或重试耗尽。
    /// </summary>
    private async Task RunAsync(ChannelWriter<GroupMessageEvent> writer, CancellationToken cancellationToken)
    {
        Exception? fatal = null;
        var consecutiveFailures = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _tracker.TryTransition(
                    ConnectionState.Connecting,
                    consecutiveFailures == 0 ? "首次连接。" : $"第 {consecutiveFailures + 1} 次连接尝试。");

                var session = await RunSessionAsync(writer, cancellationToken).ConfigureAwait(false);

                if (session.Connected)
                {
                    // 成功建立过一次连接即重置退避，避免偶发断线被长期惩罚。
                    consecutiveFailures = 0;
                }

                if (session.Outcome == SessionOutcome.Cancelled)
                {
                    break;
                }

                // 无论是被服务端关闭还是握手/接收异常，都记一次失败并进入退避。
                consecutiveFailures++;

                if (!await WaitBeforeReconnectAsync(consecutiveFailures, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            // 理论不可达；兜底捕获，保证通道一定被收尾、状态不会卡在中间态。
            fatal = ex;
            Log(OneBotDiagnosticLevel.Error, $"事件循环异常终止：{Sanitize(ex.Message)}", ex);
        }
        finally
        {
            _tracker.TryTransition(ConnectionState.Disconnected, "事件循环结束。");
            writer.TryComplete(fatal);
        }
    }

    /// <summary>
    /// 执行一次连接会话：握手、接收直到断开，并保证套接字被正确关闭。
    /// </summary>
    /// <returns>会话结果；握手异常会被就地记录并按「已完成」返回，交由上层退避重试。</returns>
    private async Task<SessionResult> RunSessionAsync(
        ChannelWriter<GroupMessageEvent> writer,
        CancellationToken cancellationToken)
    {
        ClientWebSocket? socket = null;

        try
        {
            socket = new ClientWebSocket();
            Interlocked.Exchange(ref _activeSocket, socket);

            await ConnectAsync(socket, cancellationToken).ConfigureAwait(false);
            _tracker.TryTransition(ConnectionState.Connected, "握手成功。");

            await ReceiveLoopAsync(socket, writer, cancellationToken).ConfigureAwait(false);
            Log(OneBotDiagnosticLevel.Info, "服务端关闭了连接。", null);

            return new SessionResult(SessionOutcome.Completed, Connected: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SessionResult(SessionOutcome.Cancelled, Connected: false);
        }
        catch (Exception ex)
        {
            _tracker.RecordError(Sanitize(ex.Message));
            Log(OneBotDiagnosticLevel.Error, $"连接或接收失败：{Sanitize(ex.Message)}", ex);
            return new SessionResult(SessionOutcome.Completed, Connected: false);
        }
        finally
        {
            Interlocked.CompareExchange(ref _activeSocket, null, socket);
            await SafeDisposeAsync(socket).ConfigureAwait(false);

            // 缩短明文令牌在内存中的驻留时间；异常日志已在上面完成脱敏。
            _activeToken = null;
        }
    }

    /// <summary>
    /// 进入重连状态并按退避策略等待；返回 <see langword="false"/> 表示不应再重连。
    /// </summary>
    private async Task<bool> WaitBeforeReconnectAsync(int consecutiveFailures, CancellationToken cancellationToken)
    {
        if (!_reconnectPolicy.ShouldRetry(consecutiveFailures))
        {
            Log(
                OneBotDiagnosticLevel.Warning,
                $"连续失败 {consecutiveFailures} 次，达到重试上限，停止重连。",
                null);
            return false;
        }

        _tracker.TryTransition(ConnectionState.Reconnecting, "等待退避后重连。");

        var delay = _reconnectPolicy.GetDelay(consecutiveFailures - 1);
        Log(OneBotDiagnosticLevel.Info, $"将在 {delay.TotalMilliseconds:F0} ms 后重连。", null);

        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task ConnectAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        string? token;

        try
        {
            token = _accessTokenProvider();
        }
        catch (Exception ex)
        {
            // 取令牌失败不应让整个事件循环崩掉，退化为匿名连接。
            token = null;
            Log(OneBotDiagnosticLevel.Warning, "读取访问令牌失败，将以不带 Authorization 头的方式连接。", ex);
        }

        _activeToken = token;

        if (!string.IsNullOrWhiteSpace(token))
        {
            // 令牌只进入握手头，绝不进入日志、状态或异常消息。
            socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        }

        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(KeepAliveIntervalSeconds);

        await socket.ConnectAsync(_endpoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 接收循环：按 EndOfMessage 循环拼包，完整帧到达后再解析并投递。
    /// </summary>
    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        ChannelWriter<GroupMessageEvent> writer,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveChunkSize);

        try
        {
            using var frame = new MemoryStream(ReceiveChunkSize);
            var sink = new List<GroupMessageEvent>(4);

            while ((socket.State is WebSocketState.Open or WebSocketState.CloseSent) && !cancellationToken.IsCancellationRequested)
            {
                var result = await socket
                    .ReceiveAsync(buffer.AsMemory(0, ReceiveChunkSize), cancellationToken)
                    .ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    Log(OneBotDiagnosticLevel.Info, "收到关闭帧，将转入重连流程。", null);
                    return;
                }

                if (result.Count > 0)
                {
                    if (frame.Length + result.Count > _maxMessageBytes)
                    {
                        throw new InvalidOperationException($"单条事件超过 {_maxMessageBytes} 字节上限，已中断该连接。");
                    }

                    frame.Write(buffer, 0, result.Count);
                }

                if (!result.EndOfMessage)
                {
                    // 分包：继续接收直到 EndOfMessage。
                    continue;
                }

                var frameLength = (int)frame.Length;
                DispatchFrame(new ReadOnlyMemory<byte>(frame.GetBuffer(), 0, frameLength), sink, writer);
                frame.SetLength(0);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 拆分并解析一帧，投递其中的全部群消息；解析失败只丢弃坏数据，不中断接收循环。
    /// </summary>
    /// <remarks>
    /// 职责分工：<see cref="OneBotFrameSplitter"/> 只负责切出顶层 JSON 值，
    /// <see cref="OneBotEventParser"/> 负责协议解析，<see cref="OneBotMapper"/> 负责领域映射。
    /// 白名单过滤不在这里做，由 <see cref="GroupWhitelistMatcher"/> 在下游处理。
    /// </remarks>
    /// <param name="payload">完整的一帧 UTF-8 JSON，可能包含多个顶层 JSON 值（由 <see cref="OneBotFrameSplitter"/> 拆分）。</param>
    /// <param name="sink">复用的收集列表，每次调用前由本方法清空。</param>
    /// <param name="writer">事件通道写入端。</param>
    private void DispatchFrame(ReadOnlyMemory<byte> payload, List<GroupMessageEvent> sink, ChannelWriter<GroupMessageEvent> writer)
    {
        sink.Clear();

        if (payload.IsEmpty)
        {
            return;
        }

        try
        {
            // 一帧内可能有多个顶层 JSON 值（粘包），逐个解析并映射。
            foreach (var value in OneBotFrameSplitter.Split(payload))
            {
                var result = OneBotEventParser.ParseDetailed(Encoding.UTF8.GetString(value.Span));

                if (OneBotMapper.TryMapToGroupMessageEvent(result, out var groupMessage))
                {
                    sink.Add(groupMessage);
                    continue;
                }

                NoteNonGroupMessage(result);
            }
        }
        catch (JsonException ex)
        {
            // 保留已解析出的事件，只丢弃坏掉的部分。
            Log(OneBotDiagnosticLevel.Warning, $"事件帧中存在无法解析的内容：{Sanitize(ex.Message)}", ex);
        }

        foreach (var groupMessage in sink)
        {
            // 无界通道不会满，写入必然成功，因此不需要异步等待背压。
            writer.TryWrite(groupMessage);
        }
    }

    /// <summary>
    /// 为非群消息的帧留痕。
    /// </summary>
    /// <remarks>
    /// 内嵌映射器时代只用一个 <see langword="null"/> 表示"不关心"，于是坏数据、心跳、
    /// 私聊消息在日志里完全无法区分。这里借解析器的分类把它们分开：
    /// 坏数据记 <see cref="OneBotDiagnosticLevel.Warning"/>（但不中断接收循环），
    /// meta_event 记 <see cref="OneBotDiagnosticLevel.Debug"/>（心跳是常态，不该刷屏），
    /// 私聊/通知等格式合法的其它事件保持静默。
    /// </remarks>
    /// <param name="result">解析结果；调用方已确认它不是群消息。</param>
    private void NoteNonGroupMessage(OneBotParseResult result)
    {
        switch (result.Kind)
        {
            case OneBotEventKind.Ignored:
                Log(OneBotDiagnosticLevel.Warning, $"事件帧中存在无法解析的内容：{Sanitize(result.Reason)}", null);
                break;

            case OneBotEventKind.MetaEvent:
                Log(OneBotDiagnosticLevel.Debug, $"收到 OneBot 元事件：{Sanitize(result.MetaEventType)}。", null);
                break;

            case OneBotEventKind.Other:
            case OneBotEventKind.GroupMessage:
            default:
                break;
        }
    }

    private static async ValueTask SafeDisposeAsync(ClientWebSocket? socket)
    {
        if (socket is null)
        {
            return;
        }

        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket
                    .CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // 关闭握手失败时直接放弃，走 Abort/Dispose。
        }

        socket.Dispose();
    }

    private void Log(OneBotDiagnosticLevel level, string message, Exception? exception) =>
        Diagnostics?.Invoke(new OneBotDiagnostic(level, Sanitize(message), exception));

    /// <summary>
    /// 兜底脱敏：即使未来新增日志文案，也不至于把令牌写进日志。
    /// 只使用当前连接持有的令牌副本，避免每条日志都去解密一次密钥存储。
    /// </summary>
    private string Sanitize(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return string.Empty;
        }

        var token = _activeToken;

        return string.IsNullOrEmpty(token)
            ? message
            : message.Replace(token, "***", StringComparison.Ordinal);
    }

    private sealed record RunHandle(
        Channel<GroupMessageEvent> Channel,
        CancellationTokenSource Cancellation,
        Task Task);

    /// <summary>单次连接会话的结果。</summary>
    private enum SessionOutcome
    {
        /// <summary>会话结束（服务端关闭或发生异常），应进入退避重连。</summary>
        Completed,

        /// <summary>因取消而结束，应停止重连。</summary>
        Cancelled,
    }

    /// <param name="Outcome">会话结束方式。</param>
    /// <param name="Connected">本次会话是否成功完成握手。</param>
    private readonly record struct SessionResult(SessionOutcome Outcome, bool Connected);
}
