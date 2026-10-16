using System;
using System.Globalization;
using StreamingAggregation.Core.Abstractions;

namespace StreamingAggregation.Infrastructure.ViewModels;

/// <summary>
/// 连接状态绑定：把 <see cref="ConnectionState"/> 的流转翻译成界面可直接绑定的文本与标志。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是被动的</b>：本类不订阅任何事件，也不持有 <see cref="IOneBotEventSource"/>。
/// 状态由组合根在事件源的状态回调里调用 <see cref="Apply"/> 推进——连接状态只保留一个入口，
/// 避免"托盘已经更新、界面还是旧的"这类双写遗漏。
/// </para>
/// <para>
/// <b>原因与错误必须分开存</b>：重连循环每轮都会推一条「第 N 次连接尝试。」
/// 之类的泛化原因。若把原因直接写进"最后错误"，第一次重试就会把真正的握手失败原因盖掉，
/// 用户只能看到一句「等待退避后重连。」。因此 <see cref="StatusReason"/>（可短暂）与
/// <see cref="LastErrorMessage"/>（跨重试保留，只在连上时清空）是两个独立字段。
/// </para>
/// <para>
/// <b>线程模型</b>：推送来自 OneBot 的后台线程，而 WinUI 属性必须在 UI 线程上改。构造函数
/// 因此接受一个封送委托；未提供时直接就地更新，便于在无 UI 的单元测试里直接使用。
/// </para>
/// <para>
/// <b>为什么放在 Infrastructure 而不是留在 WinUI 侧</b>：本类只依赖 Core 的 <see cref="ConnectionState"/>
/// 枚举和 BCL 的 <see cref="INotifyPropertyChanged"/>，不碰任何 UI 类型。原先它写在 App 程序集里，
/// 而测试宿主加载不了 WinUI 程序集，于是"重连次数会不会累加""状态文本对不对"这类纯逻辑只能靠手点验证。
/// 下沉之后测试项目（只引用 Core 与 Infrastructure）可以直接构造并断言。
/// </para>
/// </remarks>
public sealed class ConnectionStatusViewModel : ObservableObject
{
    private readonly Action<Action> _marshal;
    private readonly Func<DateTimeOffset> _utcNow;

    private ConnectionState _state = ConnectionState.Disconnected;
    private string _endpoint = string.Empty;
    private string? _statusReason;
    private string? _lastErrorMessage;
    private int _reconnectCount;
    private DateTimeOffset? _connectedSinceUtc;

    /// <summary>
    /// 创建连接状态视图模型。
    /// </summary>
    /// <param name="marshal">
    /// 把一段更新动作搬到 UI 线程执行的封送委托；为 <see langword="null"/> 时就地更新。
    /// </param>
    /// <param name="utcNow">当前 UTC 时间源；为 <see langword="null"/> 时使用系统时钟。</param>
    public ConnectionStatusViewModel(Action<Action>? marshal = null, Func<DateTimeOffset>? utcNow = null)
    {
        _marshal = marshal ?? (static action => action());
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>连接状态变更事件，供托盘等非绑定视图跟随更新。</summary>
    public event EventHandler<ConnectionStatusViewModel>? StatusChanged;

    /// <summary>当前连接状态。</summary>
    public ConnectionState State => _state;

    /// <summary>是否已连接。</summary>
    public bool IsConnected => _state == ConnectionState.Connected;

    /// <summary>连接端点（不含任何凭据）。</summary>
    public string Endpoint => _endpoint;

    /// <summary>
    /// 最近一次状态变更的原因；只描述"这一步为什么发生"，可被下一次状态变更覆盖。
    /// </summary>
    /// <remarks>
    /// 典型文案是「首次连接。」「第 3 次连接尝试。」「等待退避后重连。」——
    /// 它们对排障几乎没有价值，因此<strong>不写入</strong> <see cref="LastErrorMessage"/>。
    /// </remarks>
    public string? StatusReason => _statusReason;

    /// <summary>
    /// 最后一次真实错误摘要（已脱敏）；无错误时为 <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 跨重连保留，只有真正进入 <see cref="ConnectionState.Connected"/> 才清空。
    /// 这是"用户看到的是真错误还是一句泛化文案"的关键：重连循环每轮都会推
    /// 「第 N 次连接尝试。」，若与错误共用一个字段，真正的原因会在第一次重试后就被抹掉。
    /// </remarks>
    public string? LastErrorMessage => _lastErrorMessage;

    /// <summary>状态补充说明：优先给出真实错误，其次才是状态变更原因。</summary>
    public string? StatusDescription => _lastErrorMessage ?? _statusReason;

    /// <summary>本次会话累计的重连次数。</summary>
    public int ReconnectCount => _reconnectCount;

    /// <summary>最近一次进入"已连接"的时刻；从未连通过时为 <see langword="null"/>。</summary>
    public DateTimeOffset? ConnectedSinceUtc => _connectedSinceUtc;

    /// <summary>状态短标签，标题栏与托盘提示里用。</summary>
    public string StatusText => _state switch
    {
        ConnectionState.Connected => "已连接",
        ConnectionState.Connecting => "正在连接…",
        ConnectionState.Reconnecting => "正在重连…",
        _ => "未连接",
    };

    /// <summary>状态长描述：短标签 + 端点 + 状态补充说明（错误优先于原因）。</summary>
    public string DetailText
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(_endpoint)
                ? StatusText
                : $"{StatusText} · {_endpoint}";

            var detail = StatusDescription;
            return string.IsNullOrWhiteSpace(detail) ? text : $"{text} — {detail}";
        }
    }

    /// <summary>
    /// 记录当前连接使用的端点。端点在客户端构造时即已固化，设置变更后需重新调用。
    /// </summary>
    /// <param name="endpoint">形如 <c>ws://127.0.0.1:3001/</c> 的端点。</param>
    public void SetEndpoint(string? endpoint)
    {
        var normalized = endpoint ?? string.Empty;

        if (string.Equals(_endpoint, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _marshal(() =>
        {
            _endpoint = normalized;
            Refresh();
        });
    }

    /// <summary>
    /// 推送一次连接状态变更。
    /// </summary>
    /// <param name="state">新状态。</param>
    /// <param name="reason">
    /// 状态变更原因（短暂）。只描述"这一步为什么发生"，不参与错误留存。
    /// </param>
    /// <param name="lastError">
    /// 本次状态背后的真实错误（已脱敏）。非空时覆盖 <see cref="LastErrorMessage"/>；
    /// 为空时保留既有错误——重连循环推来的泛化原因不携带错误信息，
    /// 若在这里把既有错误清掉，用户就再也看不到失败原因了。
    /// </param>
    public void Apply(ConnectionState state, string? reason = null, string? lastError = null)
    {
        _marshal(() =>
        {
            var previous = _state;
            _state = state;
            _statusReason = string.IsNullOrWhiteSpace(reason) ? null : reason;

            if (!string.IsNullOrWhiteSpace(lastError))
            {
                _lastErrorMessage = lastError;
            }

            if (state == ConnectionState.Connected)
            {
                if (previous != ConnectionState.Connected)
                {
                    _connectedSinceUtc = _utcNow();
                }

                // 只有真正连上才算故障结束。注意这个清空排在上面之后：
                // 事件源会把最近一次错误原样带过来，若顺序反了，
                // 刚写进去的陈旧错误会立刻被自己抹掉。
                _lastErrorMessage = null;
            }
            else if (state == ConnectionState.Reconnecting && previous != ConnectionState.Reconnecting)
            {
                _reconnectCount++;
            }
            else if (state == ConnectionState.Disconnected)
            {
                _connectedSinceUtc = null;
            }

            Refresh();

            // 在封送块内触发：订阅者（如托盘）会直接碰 WinUI 对象，必须在 UI 线程上收到通知。
            StatusChanged?.Invoke(this, this);
        });
    }

    /// <summary>
    /// 记录一次连接错误并把状态置为"已断开"。
    /// </summary>
    /// <remarks>
    /// 供拿不到状态事件的自定义事件源兜底使用；<see cref="OneBotWebSocketClient"/>
    /// 走 <see cref="Apply"/> 的正常通道即可。
    /// </remarks>
    /// <param name="message">已脱敏的错误摘要；为空时保留既有错误。</param>
    public void ApplyError(string? message) => Apply(ConnectionState.Disconnected, reason: null, lastError: message);

    /// <summary>清空最近一次错误，不改变当前状态。</summary>
    public void ClearError() => _marshal(() =>
    {
        _lastErrorMessage = null;
        Refresh();
        StatusChanged?.Invoke(this, this);
    });

    /// <summary>把当前时间格式化为"已持续连接多久"这类可直接显示的文本。</summary>
    /// <returns>持续时长文本；未连接时返回空串。</returns>
    public string DescribeConnectedDuration()
    {
        if (_connectedSinceUtc is not { } since)
        {
            return string.Empty;
        }

        var elapsed = _utcNow() - since;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return elapsed.TotalHours >= 1
            ? string.Format(CultureInfo.CurrentCulture, "已连接 {0:0.#} 小时", elapsed.TotalHours)
            : string.Format(CultureInfo.CurrentCulture, "已连接 {0:0} 分钟", elapsed.TotalMinutes);
    }

    /// <summary>
    /// 广播全部派生属性的变更通知。
    /// </summary>
    /// <remarks>
    /// <see cref="StatusText"/>、<see cref="DetailText"/> 等是由 <see cref="State"/> 计算出来的，
    /// 没有各自的 <c>SetProperty</c>，只能在这里统一补发通知。
    /// </remarks>
    private void Refresh()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DetailText));
        OnPropertyChanged(nameof(Endpoint));
        OnPropertyChanged(nameof(StatusReason));
        OnPropertyChanged(nameof(LastErrorMessage));
        OnPropertyChanged(nameof(StatusDescription));
        OnPropertyChanged(nameof(ReconnectCount));
        OnPropertyChanged(nameof(ConnectedSinceUtc));
    }
}
