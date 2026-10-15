using System.Net.WebSockets;
using System.Text;

namespace StreamingAggregation.Tests.Fixtures;

/// <summary>
/// TestWebSocketServer 的自测用例，验证握手、请求头捕获、401 拒绝和消息推送。
/// </summary>
public sealed class TestWebSocketServerTests : IAsyncLifetime
{
    private TestWebSocketServer _server = null!;
    private ClientWebSocket _client = null!;

    public Task InitializeAsync()
    {
        _server = new TestWebSocketServer();
        return _server.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_client is not null)
        {
            _client.Dispose();
        }

        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task HandshakeCompletes_AndCapturesAuthorizationHeader()
    {
        _client = new ClientWebSocket();
        _client.Options.SetRequestHeader("Authorization", "Bearer test-token-abc");

        await _client.ConnectAsync(
            new Uri($"ws://127.0.0.1:{_server.Port}/"),
            CancellationToken.None);

        Assert.Equal(WebSocketState.Open, _client.State);
        Assert.Equal(1, _server.ConnectionCount);

        var headers = Assert.Single(_server.CapturedHeaders);
        Assert.True(headers.TryGetValue("Authorization", out var auth));
        Assert.Equal("Bearer test-token-abc", auth);
    }

    [Fact]
    public async Task ServerPushesMessage_ClientReceivesIt()
    {
        _client = new ClientWebSocket();
        await _client.ConnectAsync(
            new Uri($"ws://127.0.0.1:{_server.Port}/"),
            CancellationToken.None);

        await _server.PushMessageAsync("hello from test server");

        var buffer = new byte[1024];
        var result = await _client.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        var received = Encoding.UTF8.GetString(buffer, 0, result.Count);

        Assert.Equal("hello from test server", received);
    }

    [Fact]
    public async Task RejectWith401_ClosesConnection_DoesNotCompleteHandshake()
    {
        await using var rejectingServer = new TestWebSocketServer(rejectWith401: true);
        await rejectingServer.StartAsync();

        using var client = new ClientWebSocket();
        client.Options.SetRequestHeader("Authorization", "Bearer test-token-abc");

        await Assert.ThrowsAsync<WebSocketException>(() =>
            client.ConnectAsync(
                new Uri($"ws://127.0.0.1:{rejectingServer.Port}/"),
                CancellationToken.None));

        Assert.Equal(1, rejectingServer.ConnectionCount);
        var headers = Assert.Single(rejectingServer.CapturedHeaders);
        Assert.True(headers.TryGetValue("Authorization", out var auth));
        Assert.Equal("Bearer test-token-abc", auth);
    }

    [Fact]
    public void NoConnections_ConnectionCountIsZero()
    {
        Assert.Equal(0, _server.ConnectionCount);
    }
}
