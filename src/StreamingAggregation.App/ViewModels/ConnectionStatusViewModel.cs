using System;
using System.Globalization;
using StreamingAggregation.Core.Abstractions;

namespace StreamingAggregation.App.ViewModels;

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
/// <b>线程模型</b>：推送来自 OneBot 的后台线程，而 WinUI 属性必须在 UI 线程上改。构造函数
/// 因此接受一个封送委托；未提供时直接就地更新，便于在无 UI 的单元测试里直接使用。
/// </para>
/// </remarks>
public sealed class ConnectionStatusViewModel : ObservableObject
{
    private readonly Action<Action> _marshal;
    private readonly Func<DateTimeOffset> _utcNow;

    private ConnectionState _state = ConnectionState.Disconnected;
    private string _endpoint = string.Empty;
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

    /// <summary>最近一次错误摘要；无错误时为 <see langword="null"/>。</summary>
    public string? LastErrorMessage => _lastErrorMessage;

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

    /// <summary>状态长描述：短标签 + 端点 + 最近错误。</summary>
    public string DetailText
    {
        get
        {
            var text = string.IsNullOrWhiteSpace(_endpoint)
                ? StatusText
                : $"{StatusText} · {_endpoint}";

            return string.IsNullOrWhiteSpace(_lastErrorMessage) ? text : $"{text} — {_lastErrorMessage}";
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
    /// <param name="reason">状态变更原因；为空时保留最近一次错误。</param>
    public void Apply(ConnectionState state, string? reason = null)
    {
        _marshal(() =>
        {
            var previous = _state;
            _state = state;

            if (!string.IsNullOrWhiteSpace(reason))
            {
                _lastErrorMessage = reason;
            }

            if (state == ConnectionState.Connected)
            {
                if (previous != ConnectionState.Connected)
                {
                    _connectedSinceUtc = _utcNow();
                }

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
    /// <param name="message">已脱敏的错误摘要。</param>
    public void ApplyError(string? message)
    {
        _marshal(() =>
        {
            _state = ConnectionState.Disconnected;
            _connectedSinceUtc = null;

            if (!string.IsNullOrWhiteSpace(message))
            {
                _lastErrorMessage = message;
            }

            Refresh();
            StatusChanged?.Invoke(this, this);
        });
    }

    /// <summary>清空最近一次错误。</summary>
    public void ClearError() => ApplyError(message: null);

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
        OnPropertyChanged(nameof(LastErrorMessage));
        OnPropertyChanged(nameof(ReconnectCount));
        OnPropertyChanged(nameof(ConnectedSinceUtc));
    }
}