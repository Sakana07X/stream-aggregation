using StreamingAggregation.Core.Abstractions;
using StreamingAggregation.Infrastructure.OneBot;
using StreamingAggregation.Infrastructure.ViewModels;

namespace StreamingAggregation.Tests.ViewModels;

/// <summary>
/// <see cref="ConnectionStatusViewModel"/> 的状态与错误语义测试。
/// </summary>
/// <remarks>
/// <para>
/// 被钉住的核心区分：<b>状态变更原因</b>（<see cref="ConnectionStatusViewModel.StatusReason"/>）
/// 与 <b>最后错误</b>（<see cref="ConnectionStatusViewModel.LastErrorMessage"/>）是两个字段。
/// 两者共用一个字段时，重连循环推来的「第 N 次连接尝试。」会在第一次重试就把真正的失败原因盖掉。
/// </para>
/// <para>
/// 用例按组合根的接线方式驱动（状态事件 → <c>Apply(state, reason, tracker.LastError)</c>），
/// 因此顺带覆盖了"界面到底会看到什么"，而不只是视图模型内部的字段变化。
/// </para>
/// </remarks>
public sealed class ConnectionStatusViewModelTests
{
    private const string HandshakeError = "握手失败：服务器返回了握手响应标头中缺少 Sec-WebSocket-Accept。";

    private const string Endpoint = "ws://127.0.0.1:3001/";

    // ---------------------------------------------------------------------
    // 错误留存：迁移到 Connecting / Reconnecting 都不能清掉真实错误
    // ---------------------------------------------------------------------

    [Fact]
    public void Apply_AfterErrorRecorded_KeepsLastErrorWhenTransitioningToConnecting()
    {
        var tracker = new ConnectionStateTracker();
        var model = new ConnectionStatusViewModel();
        Wire(model, tracker);

        tracker.RecordError(HandshakeError);
        tracker.TryTransition(ConnectionState.Connecting, "首次连接。");
        Assert.Equal(HandshakeError, model.LastErrorMessage);

        // 第二轮：reason 变成泛化文案，错误必须原样留下。
        tracker.TryTransition(ConnectionState.Reconnecting, HandshakeError);
        tracker.TryTransition(ConnectionState.Connecting, "第 2 次连接尝试。");

        Assert.Equal(ConnectionState.Connecting, model.State);
        Assert.Equal(HandshakeError, model.LastErrorMessage);
        Assert.Equal("第 2 次连接尝试。", model.StatusReason);
        Assert.Equal(HandshakeError, model.StatusDescription);
    }

    [Fact]
    public void Apply_AfterErrorRecorded_KeepsLastErrorWhenTransitioningToReconnecting()
    {
        var tracker = new ConnectionStateTracker();
        var model = new ConnectionStatusViewModel();
        Wire(model, tracker);

        tracker.RecordError(HandshakeError);
        tracker.TryTransition(ConnectionState.Connecting, "首次连接。");
        tracker.TryTransition(ConnectionState.Reconnecting, HandshakeError);
        Assert.Equal(HandshakeError, model.LastErrorMessage);

        // 退避等待阶段的流程文案不得顶掉真实错误。
        tracker.TryTransition(ConnectionState.Connecting, "第 2 次连接尝试。");
        tracker.TryTransition(ConnectionState.Reconnecting, "等待退避后重连。");

        Assert.Equal(ConnectionState.Reconnecting, model.State);
        Assert.Equal(HandshakeError, model.LastErrorMessage);
        Assert.Equal("等待退避后重连。", model.StatusReason);
        Assert.Equal(HandshakeError, model.StatusDescription);
    }

    [Fact]
    public void Apply_WhenReconnected_ClearsLastError()
    {
        var tracker = new ConnectionStateTracker();
        var model = new ConnectionStatusViewModel();
        Wire(model, tracker);

        tracker.RecordError(HandshakeError);
        tracker.TryTransition(ConnectionState.Connecting, "首次连接。");
        tracker.TryTransition(ConnectionState.Reconnecting, HandshakeError);
        Assert.NotNull(model.LastErrorMessage);

        // 事件源把陈旧的 LastError 一起带过来（tracker 自己不会在连上时清它），
        // 视图模型必须以「已连接」这一事实为准把它清掉。
        tracker.TryTransition(ConnectionState.Connecting, "第 2 次连接尝试。");
        tracker.TryTransition(ConnectionState.Connected, "握手成功。");
        Assert.Null(model.LastErrorMessage);

        // 错误没了，但没有原因可显示时才真的什么都不显示——这里是「握手成功。」。
        Assert.Equal("握手成功。", model.StatusDescription);

        // 再来一轮失败：错误重新出现，连上后才再次消失。
        tracker.RecordError("第 2 次握手的错误。");
        tracker.TryTransition(ConnectionState.Reconnecting, "第 2 次握手的错误。");
        Assert.Equal("第 2 次握手的错误。", model.LastErrorMessage);

        tracker.TryTransition(ConnectionState.Connecting, "第 3 次连接尝试。");
        tracker.TryTransition(ConnectionState.Connected, "握手成功。");
        Assert.Null(model.LastErrorMessage);
    }

    [Fact]
    public void Apply_WithoutNewError_KeepsPreviousLastError()
    {
        var model = new ConnectionStatusViewModel();

        model.Apply(ConnectionState.Reconnecting, "等待退避后重连。", HandshakeError);
        model.Apply(ConnectionState.Connecting, "第 2 次连接尝试。");

        // 未携带错误的那一次变更不能把错误清掉。
        Assert.Equal(HandshakeError, model.LastErrorMessage);
    }

    [Fact]
    public void Apply_BlankReasonOrError_IsTreatedAsAbsent()
    {
        var model = new ConnectionStatusViewModel();

        model.Apply(ConnectionState.Reconnecting, "等待退避后重连。", HandshakeError);
        model.Apply(ConnectionState.Connecting, "   ");

        Assert.Null(model.StatusReason);
        Assert.Equal(HandshakeError, model.LastErrorMessage);
    }

    // ---------------------------------------------------------------------
    // 界面文本：真实错误优先于泛化原因
    // ---------------------------------------------------------------------

    [Fact]
    public void DetailText_PrefersRealErrorOverGenericAttemptReason()
    {
        var tracker = new ConnectionStateTracker();
        var model = new ConnectionStatusViewModel();
        Wire(model, tracker);
        model.SetEndpoint(Endpoint);

        tracker.RecordError(HandshakeError);
        tracker.TryTransition(ConnectionState.Connecting, "首次连接。");
        tracker.TryTransition(ConnectionState.Reconnecting, HandshakeError);
        tracker.TryTransition(ConnectionState.Connecting, "第 2 次连接尝试。");

        // 这正是被修复的那条覆盖链：重试轮次的泛化文案曾把握手失败原因顶掉。
        Assert.Contains(HandshakeError, model.DetailText, StringComparison.Ordinal);
        Assert.DoesNotContain("第 2 次连接尝试。", model.DetailText, StringComparison.Ordinal);
        Assert.Contains(Endpoint, model.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailText_FallsBackToStatusReason_WhenNoErrorRecorded()
    {
        var model = new ConnectionStatusViewModel();

        model.Apply(ConnectionState.Connecting, "首次连接。");

        Assert.Null(model.LastErrorMessage);
        Assert.Equal("首次连接。", model.StatusDescription);
        Assert.Contains("首次连接。", model.DetailText, StringComparison.Ordinal);
    }

    [Fact]
    public void DetailText_OmitsDetail_WhenNeitherErrorNorReasonPresent()
    {
        var model = new ConnectionStatusViewModel();
        model.SetEndpoint(Endpoint);

        model.Apply(ConnectionState.Connecting);

        Assert.Null(model.StatusDescription);
        Assert.Equal($"正在连接… · {Endpoint}", model.DetailText);
    }

    // ---------------------------------------------------------------------
    // 兜底入口、统计与通知
    // ---------------------------------------------------------------------

    [Fact]
    public void ApplyError_MovesToDisconnected_AndRecordsError()
    {
        var model = new ConnectionStatusViewModel();

        model.Apply(ConnectionState.Connected, "握手成功。");
        model.ApplyError(HandshakeError);

        Assert.Equal(ConnectionState.Disconnected, model.State);
        Assert.False(model.IsConnected);
        Assert.Null(model.ConnectedSinceUtc);
        Assert.Equal(HandshakeError, model.LastErrorMessage);
    }

    [Fact]
    public void ClearError_DropsLastErrorWithoutChangingState()
    {
        var model = new ConnectionStatusViewModel();

        model.Apply(ConnectionState.Reconnecting, "等待退避后重连。", HandshakeError);
        model.ClearError();

        Assert.Null(model.LastErrorMessage);
        Assert.Equal(ConnectionState.Reconnecting, model.State);

        // 清掉之后再喂一条空白错误，也不能凭空造出一条错误。
        model.ApplyError("   ");
        Assert.Null(model.LastErrorMessage);
    }

    [Fact]
    public void Apply_CountsReconnectsOnlyOnActualReconnects()
    {
        var model = new ConnectionStatusViewModel();

        model.Apply(ConnectionState.Connecting, "首次连接。");
        model.Apply(ConnectionState.Reconnecting, HandshakeError);
        model.Apply(ConnectionState.Connecting, "第 2 次连接尝试。");
        model.Apply(ConnectionState.Reconnecting, HandshakeError);
        model.Apply(ConnectionState.Reconnecting, "等待退避后重连。");

        Assert.Equal(2, model.ReconnectCount);
    }

    [Fact]
    public void Apply_MarshalsTheWholeUpdateOnce()
    {
        var marshals = 0;
        var model = new ConnectionStatusViewModel(_ => marshals++);

        model.Apply(ConnectionState.Reconnecting, "等待退避后重连。", HandshakeError);

        // 封送只发生一次：字段更新、通知与 StatusChanged 都在同一个 UI 线程块里完成，
        // 订阅者（托盘）因此不会看到半更新状态。
        Assert.Equal(1, marshals);
    }

    [Fact]
    public void Apply_RaisesNotificationsForNewAndRetainedFields()
    {
        var model = new ConnectionStatusViewModel();
        var notifications = new List<string?>();
        model.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);

        model.Apply(ConnectionState.Reconnecting, "等待退避后重连。", HandshakeError);

        Assert.Contains(nameof(ConnectionStatusViewModel.StatusReason), notifications);
        Assert.Contains(nameof(ConnectionStatusViewModel.LastErrorMessage), notifications);
        Assert.Contains(nameof(ConnectionStatusViewModel.StatusDescription), notifications);
        Assert.Contains(nameof(ConnectionStatusViewModel.DetailText), notifications);
    }

    [Fact]
    public void StatusChanged_FiresOnEveryApply()
    {
        var model = new ConnectionStatusViewModel();
        var observed = new List<ConnectionState>();
        model.StatusChanged += (_, current) => observed.Add(current.State);

        model.Apply(ConnectionState.Connecting, "首次连接。");
        model.Apply(ConnectionState.Reconnecting, HandshakeError);
        model.Apply(ConnectionState.Connected, "握手成功。");

        Assert.Equal(
            [ConnectionState.Connecting, ConnectionState.Reconnecting, ConnectionState.Connected],
            observed);
    }

    [Fact]
    public void SetEndpoint_KeepsLastErrorAndIsIdempotent()
    {
        var model = new ConnectionStatusViewModel();

        model.Apply(ConnectionState.Reconnecting, "等待退避后重连。", HandshakeError);
        model.SetEndpoint(Endpoint);
        Assert.Equal(HandshakeError, model.LastErrorMessage);

        model.SetEndpoint(Endpoint);
        Assert.Equal(Endpoint, model.Endpoint);
    }

    [Fact]
    public void Apply_TracksConnectedSinceAndDuration()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var model = new ConnectionStatusViewModel(utcNow: () => now);

        model.Apply(ConnectionState.Connected, "握手成功。");
        Assert.Equal(now, model.ConnectedSinceUtc);

        now = now.AddMinutes(45);
        Assert.Equal("已连接 45 分钟", model.DescribeConnectedDuration());

        now = now.AddMinutes(45);
        Assert.Equal("已连接 1.5 小时", model.DescribeConnectedDuration());

        // 断开后不再显示时长。
        model.Apply(ConnectionState.Disconnected, "服务端关闭了连接。");
        Assert.Null(model.ConnectedSinceUtc);
        Assert.Equal(string.Empty, model.DescribeConnectedDuration());
    }

    // ---------------------------------------------------------------------
    // 测试脚手架
    // ---------------------------------------------------------------------

    /// <summary>
    /// 按组合根的方式接线：状态变化时把 reason 与 <c>tracker.LastError</c> 一起推进视图模型。
    /// </summary>
    /// <remarks>
    /// 与 <c>AppCompositionRoot.OnConnectionStateChanged</c> 保持同一形状——接线写错时这里也会跟着错，
    /// 两条防线不会互相掩护。
    /// </remarks>
    private static void Wire(ConnectionStatusViewModel model, ConnectionStateTracker tracker) =>
        tracker.StateChanged += (_, e) => model.Apply(e.Current, e.Reason, tracker.LastError);
}