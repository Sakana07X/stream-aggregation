using StreamingAggregation.Core.Abstractions;

namespace StreamingAggregation.Infrastructure.OneBot;

/// <summary>连接状态变更事件参数。</summary>
public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    /// <summary>创建状态变更事件参数。</summary>
    /// <param name="previous">变更前的状态。</param>
    /// <param name="current">变更后的状态。</param>
    /// <param name="timestampUtc">变更发生时间（UTC）。</param>
    /// <param name="reason">变更原因描述，可为 <see langword="null"/>。</param>
    public ConnectionStateChangedEventArgs(
        ConnectionState previous,
        ConnectionState current,
        DateTimeOffset timestampUtc,
        string? reason)
    {
        Previous = previous;
        Current = current;
        TimestampUtc = timestampUtc;
        Reason = reason;
    }

    /// <summary>变更前的状态。</summary>
    public ConnectionState Previous { get; }

    /// <summary>变更后的状态。</summary>
    public ConnectionState Current { get; }

    /// <summary>变更发生时间（UTC）。</summary>
    public DateTimeOffset TimestampUtc { get; }

    /// <summary>变更原因描述。</summary>
    public string? Reason { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"{Previous} -> {Current}{(Reason is null ? string.Empty : $"（{Reason}）")}";
}

/// <summary>连接状态快照，供 UI 绑定或诊断输出使用。</summary>
/// <param name="State">当前状态。</param>
/// <param name="ChangedAtUtc">当前状态开始时间（UTC）。</param>
/// <param name="StateDuration">当前状态已持续时长。</param>
/// <param name="ConnectAttemptCount">进入 Connecting 的累计次数。</param>
/// <param name="SuccessfulConnectionCount">进入 Connected 的累计次数。</param>
/// <param name="ReconnectCount">进入 Reconnecting 的累计次数。</param>
/// <param name="DisconnectCount">进入 Disconnected 的累计次数。</param>
/// <param name="LastError">最近一次错误摘要，可能为 <see langword="null"/>。</param>
public sealed record ConnectionStateSnapshot(
    ConnectionState State,
    DateTimeOffset ChangedAtUtc,
    TimeSpan StateDuration,
    int ConnectAttemptCount,
    int SuccessfulConnectionCount,
    int ReconnectCount,
    int DisconnectCount,
    string? LastError);

/// <summary>
/// OneBot 连接状态跟踪器：维护当前 <see cref="ConnectionState"/>、
/// 校验状态迁移合法性、累计连接统计，并对外广播状态变更事件。
/// </summary>
/// <remarks>
/// 所有成员均为线程安全：读操作无锁（基于 <see cref="Volatile"/>），写操作在内部锁内完成。
/// 事件在锁外触发，避免订阅方回调重入导致死锁。
/// </remarks>
public sealed class ConnectionStateTracker
{
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _utcNow;

    private int _state;
    private DateTimeOffset _changedAtUtc;
    private int _connectAttemptCount;
    private int _successfulConnectionCount;
    private int _reconnectCount;
    private int _disconnectCount;
    private string? _lastError;
    private DateTimeOffset? _lastErrorAtUtc;

    /// <summary>
    /// 创建状态跟踪器。
    /// </summary>
    /// <param name="initialState">初始状态，默认 <see cref="Core.Abstractions.ConnectionState.Disconnected"/>。</param>
    /// <param name="utcNow">取当前时间（UTC）的委托，测试可注入；默认 <see cref="DateTimeOffset.UtcNow"/>。</param>
    public ConnectionStateTracker(
        ConnectionState initialState = ConnectionState.Disconnected,
        Func<DateTimeOffset>? utcNow = null)
    {
        _state = (int)initialState;
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
        _changedAtUtc = _utcNow();
    }

    /// <summary>状态变更事件，仅在状态真正发生改变时触发。</summary>
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged;

    /// <summary>当前连接状态。</summary>
    public ConnectionState Current => (ConnectionState)Volatile.Read(ref _state);

    /// <summary>当前状态开始时间（UTC）。</summary>
    public DateTimeOffset ChangedAtUtc
    {
        get
        {
            lock (_gate)
            {
                return _changedAtUtc;
            }
        }
    }

    /// <summary>当前状态已持续时长。</summary>
    public TimeSpan StateDuration
    {
        get
        {
            lock (_gate)
            {
                return _utcNow() - _changedAtUtc;
            }
        }
    }

    /// <summary>进入 Connecting 的累计次数。</summary>
    public int ConnectAttemptCount
    {
        get { lock (_gate) { return _connectAttemptCount; } }
    }

    /// <summary>进入 Connected 的累计次数。</summary>
    public int SuccessfulConnectionCount
    {
        get { lock (_gate) { return _successfulConnectionCount; } }
    }

    /// <summary>进入 Reconnecting 的累计次数。</summary>
    public int ReconnectCount
    {
        get { lock (_gate) { return _reconnectCount; } }
    }

    /// <summary>进入 Disconnected 的累计次数。</summary>
    public int DisconnectCount
    {
        get { lock (_gate) { return _disconnectCount; } }
    }

    /// <summary>最近一次错误摘要（已由调用方脱敏），无错误时为 <see langword="null"/>。</summary>
    public string? LastError
    {
        get { lock (_gate) { return _lastError; } }
    }

    /// <summary>最近一次错误发生时间（UTC），无错误时为 <see langword="null"/>。</summary>
    public DateTimeOffset? LastErrorAtUtc
    {
        get { lock (_gate) { return _lastErrorAtUtc; } }
    }

    /// <summary>
    /// 判断一次状态迁移是否被允许。
    /// </summary>
    /// <remarks>
    /// 允许的迁移：
    /// <list type="bullet">
    ///   <item><description>Disconnected → Connecting</description></item>
    ///   <item><description>Connecting → Connected / Reconnecting / Disconnected</description></item>
    ///   <item><description>Connected → Reconnecting / Disconnected</description></item>
    ///   <item><description>Reconnecting → Connecting / Disconnected</description></item>
    /// </list>
    /// 迁移到自身被视为无操作，返回 <see langword="false"/> 且不触发事件。
    /// </remarks>
    /// <param name="from">当前状态。</param>
    /// <param name="to">目标状态。</param>
    /// <returns>允许迁移返回 <see langword="true"/>。</returns>
    public static bool IsValidTransition(ConnectionState from, ConnectionState to) => from switch
    {
        ConnectionState.Disconnected => to is ConnectionState.Connecting,
        ConnectionState.Connecting => to is ConnectionState.Connected or ConnectionState.Reconnecting or ConnectionState.Disconnected,
        ConnectionState.Connected => to is ConnectionState.Reconnecting or ConnectionState.Disconnected,
        ConnectionState.Reconnecting => to is ConnectionState.Connecting or ConnectionState.Disconnected,
        _ => false,
    };

    /// <summary>
    /// 尝试迁移到目标状态；迁移非法或目标与当前相同时返回 <see langword="false"/>。
    /// </summary>
    /// <param name="next">目标状态。</param>
    /// <param name="reason">变更原因描述。</param>
    /// <returns>状态确实发生改变返回 <see langword="true"/>。</returns>
    public bool TryTransition(ConnectionState next, string? reason = null)
    {
        ConnectionStateChangedEventArgs? args;

        lock (_gate)
        {
            var previous = (ConnectionState)_state;

            if (previous == next || !IsValidTransition(previous, next))
            {
                return false;
            }

            _state = (int)next;
            _changedAtUtc = _utcNow();

            switch (next)
            {
                case ConnectionState.Connecting:
                    _connectAttemptCount++;
                    break;
                case ConnectionState.Connected:
                    _successfulConnectionCount++;
                    break;
                case ConnectionState.Reconnecting:
                    _reconnectCount++;
                    break;
                case ConnectionState.Disconnected:
                    _disconnectCount++;
                    break;
                default:
                    break;
            }

            args = new ConnectionStateChangedEventArgs(previous, next, _changedAtUtc, reason);
        }

        StateChanged?.Invoke(this, args);
        return true;
    }

    /// <summary>
    /// 记录一次错误摘要。传入的消息应已脱敏，调用方不得写入访问令牌等敏感信息。
    /// </summary>
    /// <param name="error">错误摘要。</param>
    public void RecordError(string? error)
    {
        lock (_gate)
        {
            _lastError = string.IsNullOrWhiteSpace(error) ? null : error;
            _lastErrorAtUtc = _lastError is null ? null : _utcNow();
        }
    }

    /// <summary>
    /// 记录一次错误，仅保存异常类型与消息，不保留堆栈。
    /// </summary>
    /// <param name="exception">异常实例。</param>
    public void RecordError(Exception? exception)
    {
        RecordError(exception is null ? null : $"{exception.GetType().Name}: {exception.Message}");
    }

    /// <summary>清空错误摘要。</summary>
    public void ClearError() => RecordError((string?)null);

    /// <summary>导出当前状态与统计的不可变快照。</summary>
    /// <returns>状态快照。</returns>
    public ConnectionStateSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new ConnectionStateSnapshot(
                (ConnectionState)_state,
                _changedAtUtc,
                _utcNow() - _changedAtUtc,
                _connectAttemptCount,
                _successfulConnectionCount,
                _reconnectCount,
                _disconnectCount,
                _lastError);
        }
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var snapshot = Snapshot();
        return $"{snapshot.State}（连接 {snapshot.SuccessfulConnectionCount} 次 / 尝试 {snapshot.ConnectAttemptCount} 次 / 重连 {snapshot.ReconnectCount} 次）";
    }
}