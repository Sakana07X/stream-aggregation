using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Core.Configuration;
using StreamingAggregation.Core.Diagnostics;
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
///   <item><description>令牌委托返回 <see langword="null"/> 或空串是<strong>合法状态</strong>（本地 SnowLuma 可不配 access token），照常匿名连接；委托<strong>抛异常</strong>则 fail-closed：抛 <see cref="OneBotAccessTokenException"/> 并中止本次连接，绝不退化为匿名连接。</description></item>
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
    private const int ReceiveChunkSize = 8 * 1024;
    private const int DefaultMaxMessageBytes = 4 * 1024 * 1024;
    private const int KeepAliveIntervalSeconds = 30;

    /// <summary>异常链最多向下遍历的层数（含最外层）；同时挡住 InnerException 自引用导致的死循环。</summary>
    private const int MaxErrorChainHops = 8;

    /// <summary>一条错误摘要最多拼接几段「有区分度」的消息；再多对排障没有额外收益，只会撑长状态栏。</summary>
    private const int MaxErrorChainSegments = 3;

    /// <summary>错误摘要的总长上限（字符）；拼接后仍超长即截断，作为最后一道防线。</summary>
    private const int MaxErrorReasonLength = 512;

    /// <summary>
    /// 拼接异常链各段时使用的分隔符，读作「A ← B」= A 是被 B 造成的，与异常包裹方向一致。
    /// </summary>
    private const string ErrorChainSeparator = " ← ";

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
    /// 每次连接尝试都会调用一次。委托抛异常时按 fail-closed 处理：
    /// 抛 <see cref="OneBotAccessTokenException"/> 中止本次连接，而不是匿名重连。
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
        Guard.NotNull(settings, nameof(settings));
        Guard.NotNull(accessTokenProvider, nameof(accessTokenProvider));

        Guard.ThrowIfLessThanOrEqual(maxMessageBytes, 0, "单帧上限必须大于零。");

        _endpoint = BuildEndpoint(settings.Host, settings.Port, settings.UseSsl);
        _accessTokenProvider = accessTokenProvider;
        _reconnectPolicy = reconnectPolicy ?? ReconnectPolicy.Default;
        _maxMessageBytes = maxMessageBytes;
        Diagnostics = diagnostics;

        Diagnostics?.Invoke(new OneBotDiagnostic(OneBotDiagnosticLevel.Info, $"OneBot 事件源已创建，端点 {_endpoint}。"));
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
        Guard.ThrowIfDisposed(Volatile.Read(ref _disposed), GetType().FullName);

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
        // net48 没有 Uri.UriSchemeWs / Uri.UriSchemeWss（.NET Core 2.1+），写 RFC 6455 的字面量。
        return new UriBuilder(useSsl ? "wss" : "ws", host, port, "/").Uri;
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
            Guard.ThrowIfDisposed(_disposed, GetType().FullName);

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
                // 这里的 reason 只回答「这一步为什么发生」，不是错误：组合根会另外把
                // Tracker.LastError 带给界面，因此上一次的真实失败原因不会被这句覆盖。
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
            var reason = $"事件循环异常终止：{Sanitize(ex.Message)}";
            _tracker.RecordError(reason);
            Log(OneBotDiagnosticLevel.Error, reason, ex);
        }
        finally
        {
            // 「事件循环结束。」只在没有真实错误时使用：否则这句收尾文案会把真正的失败原因盖掉，
            // 而此刻这恰恰是用户唯一能看到的东西。
            _tracker.TryTransition(ConnectionState.Disconnected, _tracker.LastError ?? "事件循环结束。");
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
        catch (OneBotAccessTokenException ex)
        {
            // 令牌读不到是配置/密钥库问题，不是网络抖动：原因原样推给状态跟踪器，
            // 用户在界面上能直接看到「令牌读取失败」而不是一句含糊的「等待重连」。
            // 消息由异常类型固定提供，本身不含令牌；这里再过一次 Sanitize 是兜底。
            var reason = Sanitize(ex.Message);
            _tracker.RecordError(reason);
            _tracker.TryTransition(ConnectionState.Reconnecting, reason);
            Log(OneBotDiagnosticLevel.Error, $"连接已中止：{reason}", ex);

            // 仍按「本次会话结束」返回，退避重连节奏交给外层，密钥恢复后可以自愈。
            return new SessionResult(SessionOutcome.Completed, Connected: false);
        }
        catch (Exception ex)
        {
            // 用户自己按下停止，不是一次连接失败，必须在记账之前挡掉。
            //
            // net48 上取消一个「正在进行」的 ClientWebSocket.ConnectAsync 抛的并不是
            // OperationCanceledException（上面那个 catch 因此接不住），实测是：
            //   WebSocketException("无法连接到远程服务器")
            //   ← WebException("请求被中止: 请求已被取消。")  [Status = RequestCanceled]
            // 形状和真正的握手失败一模一样，于是「我要停机」被记成一条错误，
            // 把上一次真实失败的原因顶掉——用户停个连接就再也看不到刚才错在哪了。
            // 连接失败与主动取消同帧发生时，以令牌为准：真正的失败早就被 RecordError 记下，
            // 这里什么都不写也不会丢；而记一条取消噪声则一定是以假乱真。
            if (cancellationToken.IsCancellationRequested)
            {
                return new SessionResult(SessionOutcome.Cancelled, Connected: false);
            }

            // 必须走异常链而不是单取 ex.Message：net48 上握手 401 的顶层消息被 BCL 泛化成
            // 「无法连接到远程服务器」，401 / Unauthorized 只存在于 InnerException 里。
            var reason = BuildFailureReason(ex);
            _tracker.RecordError(reason);

            // 真实错误必须同时走状态事件，只 RecordError 是不够的：
            // 组合根只订阅 StateChanged，错误只落在 tracker 里的话界面根本收不到，
            // 用户看到的仍然是「等待退避后重连。」。
            // 若此处已经是 Reconnecting，这次迁移会被 tracker 当作无操作丢弃；
            // 但错误仍留在 Tracker.LastError 中，下一次迁移会由组合根带出去，因此不会丢。
            _tracker.TryTransition(ConnectionState.Reconnecting, reason);
            Log(OneBotDiagnosticLevel.Error, $"连接或接收失败：{reason}", ex);
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

        // 同上：这是流程文案，不是错误原因。此刻 Tracker.LastError 里已经躺着真正的失败摘要，
        // 组合根会把它一起推给界面。若这次迁移是空操作（已经是 Reconnecting），
        // 真实错误已由本次会话内的迁移推过一次，不会因此丢失。
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
            // fail-closed：「读不到令牌」不等于「没有令牌」。前者多半是密钥库/DPAPI 出问题，
            // 退化成匿名连接只会把真正的故障掩盖成一次莫名其妙的握手失败。
            // 空串或 null 仍然照常放行——本地 SnowLuma 未配 access token 是合法场景。
            throw new OneBotAccessTokenException(ex);
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
                // net48 的 ClientWebSocket.ReceiveAsync 只收 ArraySegment<byte>；
                // 用数组分段表达同一段缓冲区，不产生拷贝。
                var result = await socket
                    .ReceiveAsync(new ArraySegment<byte>(buffer, 0, ReceiveChunkSize), cancellationToken)
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
                // net48 的 Encoding.GetString 只收 byte[]（ReadOnlySpan<byte> 重载是 .NET Core 2.1+）。
                // 每个顶层 JSON 值一次拷贝：帧最大 4 MiB，且只在解码时发生一次，可接受。
                var result = OneBotEventParser.ParseDetailed(Encoding.UTF8.GetString(value.ToArray()));

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

        // net48 没有 string.Replace(string, string, StringComparison)（.NET Core 2.1+）。
        // 两参数的 string.Replace 本来就是序数比较，语义与 StringComparison.Ordinal 完全一致。
        // 同理 string.IsNullOrEmpty 在 net48 上没有 [NotNullWhen(false)] 标注，
        // 编译器不会自动收窄 message / token，这里显式收一次。
        return string.IsNullOrEmpty(token)
            ? message!
            : message!.Replace(token!, "***");
    }

    /// <summary>
    /// 把异常及其 <see cref="Exception.InnerException"/> 链压成一条用户可见的失败原因。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 只取 <see cref="Exception.Message"/> 在 <c>net48</c> 上会丢掉真正的原因。实测：
    /// 握手被 401 拒绝时异常链是 <c>WebSocketException("无法连接到远程服务器")
    /// ← WebException("远程服务器返回错误：(401) 未经授权。")</c>——
    /// 状态码与「未经授权」全在里层，顶层被 BCL 泛化掉了。
    /// 用户填错令牌或填错端口时，界面上就只剩一句废话。
    /// </para>
    /// <para>三条约束，缺一不可：</para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <strong>逐层脱敏</strong>：内层异常同样可能带 URL（含 userinfo）、<c>Bearer</c> 头或令牌明文，
    ///     因此每一段都单独过一次 <see cref="Sanitize"/>，而不是只脱敏最外层。
    ///   </description></item>
    ///   <item><description>
    ///     <strong>逐层去重</strong>：包装异常经常把内层的消息原样复述一遍
    ///     （端口不通时 <c>WebSocketException</c> 与 <c>WebException</c> 两层都是「无法连接到远程服务器」），
    ///     无脑拼接只会让同一句话出现两遍，并不更清楚。去重按脱敏<em>之后</em>的文本做序数比较，
    ///     这样「仅差一个令牌」的两段也会正确折叠成一段。
    ///   </description></item>
    ///   <item><description>
    ///     <strong>长度封顶</strong>：只取前 <see cref="MaxErrorChainSegments"/> 段有区分度的消息，
    ///     遍历本身再限 <see cref="MaxErrorChainHops"/> 跳（异常链可能是 <c>AggregateException</c> 摊平的，
    ///     也可能自引用），最后对总长兜底截断。
    ///   </description></item>
    /// </list>
    /// <para>
    /// <strong>只追加、不替换</strong>：最外层永远是第一段，因此 <c>net10</c> 上
    /// 「顶层消息已说清状态码」的那条行为不受影响，内层只是追加的佐证；
    /// 真正依赖内层补信息的只有 <c>net48</c>。
    /// </para>
    /// <para>
    /// 方法本身是 <c>internal</c> 而非 <c>private</c>：上面三条约束没法经公开 API 构造出
    /// 对应的异常链（真实 BCL 链最多三层、且顶层与二层常常一字不差），
    /// 而它们恰恰是「异常链不会把状态栏撑爆」的唯一保险。
    /// 项目已对测试程序集开放 internal，因此可以直接用合成链把这三条钉死。
    /// </para>
    /// </remarks>
    /// <param name="exception">捕获到的异常。</param>
    /// <returns>已脱敏、可直接展示的失败原因。</returns>
    internal string BuildFailureReason(Exception exception)
    {
        var segments = new List<string>(MaxErrorChainSegments);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        Exception? current = exception;

        for (var hops = 0;
             current is not null && hops < MaxErrorChainHops && segments.Count < MaxErrorChainSegments;
             hops++)
        {
            var message = Sanitize(current.Message).Trim();

            // seen.Add 返回 true 表示「此前没见过」。重复消息直接跳过，且不占用段数预算。
            if (message.Length > 0 && seen.Add(message))
            {
                segments.Add(message);
            }

            current = current.InnerException;
        }

        if (segments.Count == 0)
        {
            // 理论上到不了这里（BCL 异常总有非空消息）。真到了也不能给状态栏塞空串：
            // 退回类型名，至少还看得出是哪一类错。
            return Sanitize(exception.GetType().Name);
        }

        var reason = string.Join(ErrorChainSeparator, segments);

        return reason.Length <= MaxErrorReasonLength
            ? reason
            : reason.Substring(0, MaxErrorReasonLength) + "…";
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
