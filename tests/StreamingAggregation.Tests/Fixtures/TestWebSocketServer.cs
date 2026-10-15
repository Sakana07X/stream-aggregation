using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace StreamingAggregation.Tests.Fixtures;

/// <summary>
/// 基于 TcpListener 的最小 WebSocket 测试服务器，用于集成测试。
/// </summary>
/// <remarks>
/// <para>监听 127.0.0.1 上的随机端口，完成标准 WebSocket 握手（RFC 6455）。</para>
/// <para>每个 TCP 连接都会被计数，所有请求头被捕获，可配置为在握手阶段返回 401。</para>
/// </remarks>
public sealed class TestWebSocketServer : IAsyncDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const byte TextFrameOpcode = 0x1;
    private const byte PingFrameOpcode = 0x9;
    private const byte PongFrameOpcode = 0xA;
    private const byte CloseFrameOpcode = 0x8;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentBag<Dictionary<string, string>> _capturedHeaders = new();
    private readonly ConcurrentBag<string> _receivedMessages = new();
    private readonly List<TcpClient> _connectedClients = new();
    private readonly object _clientsLock = new();
    private Task? _acceptLoopTask;
    private int _connectionCount;
    private bool _started;
    private bool _disposed;

    /// <param name="rejectWith401">握手阶段是否返回 401 拒绝连接。</param>
    public TestWebSocketServer(bool rejectWith401 = false)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        RejectWith401 = rejectWith401;
    }

    /// <summary>是否以 401 拒绝握手。</summary>
    public bool RejectWith401 { get; }

    /// <summary>实际绑定的端口号（调用 <see cref="StartAsync"/> 后可用）。</summary>
    public int Port { get; private set; }

    /// <summary>已接受的 TCP 连接总数。</summary>
    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    /// <summary>所有连接捕获到的请求头集合。</summary>
    public IReadOnlyCollection<Dictionary<string, string>> CapturedHeaders => _capturedHeaders.ToArray();

    /// <summary>从客户端接收到的文本消息。</summary>
    public IReadOnlyCollection<string> ReceivedMessages => _receivedMessages.ToArray();

    /// <summary>启动服务器，开始监听连接。</summary>
    public Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return Task.CompletedTask;
        }

        _started = true;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoopTask = AcceptLoopAsync(_cts.Token);
        return Task.CompletedTask;
    }

    /// <summary>向所有已连接的客户端广播一条文本消息。</summary>
    public async Task PushMessageAsync(string message, CancellationToken cancellationToken = default)
    {
        var frame = EncodeTextFrame(message);
        List<TcpClient> clients;
        lock (_clientsLock)
        {
            clients = _connectedClients.ToList();
        }

        foreach (var client in clients)
        {
            try
            {
                await client.GetStream().WriteAsync(frame, cancellationToken);
            }
            catch
            {
                // 客户端已断开，忽略。
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                _ = HandleClientAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                // listener 已停止。
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _connectionCount);
        var stream = client.GetStream();

        Dictionary<string, string>? headers = null;
        try
        {
            headers = await ReadHttpHeadersAsync(stream, cancellationToken);
            _capturedHeaders.Add(headers);

            if (RejectWith401)
            {
                await Send401ResponseAsync(stream, cancellationToken);
                return;
            }

            if (!headers.TryGetValue("Sec-WebSocket-Key", out var key) || string.IsNullOrEmpty(key))
            {
                await Send400ResponseAsync(stream, cancellationToken);
                return;
            }

            await Send101ResponseAsync(stream, ComputeAcceptKey(key), cancellationToken);

            lock (_clientsLock)
            {
                _connectedClients.Add(client);
            }

            try
            {
                await ReadFramesLoopAsync(stream, cancellationToken);
            }
            catch
            {
                // 读取帧时出错（客户端断开等），静默处理。
            }
            finally
            {
                lock (_clientsLock)
                {
                    _connectedClients.Remove(client);
                }
            }
        }
        catch
        {
            // 握手前出错（客户端断开等），静默处理。
        }
        finally
        {
            client.Close();
        }
    }

    private async Task ReadFramesLoopAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await ReadFrameAsync(stream, cancellationToken);
            if (frame is null)
            {
                break;
            }

            var f = frame.Value;
            if (f.Opcode == PingFrameOpcode)
            {
                await SendPongAsync(stream, f.Payload, cancellationToken);
            }
            else if (f.Opcode == TextFrameOpcode)
            {
                _receivedMessages.Add(Encoding.UTF8.GetString(f.Payload));
            }
            else if (f.Opcode == CloseFrameOpcode)
            {
                await SendCloseAsync(stream, cancellationToken);
                break;
            }
        }
    }

    private async Task<Frame?> ReadFrameAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var header = await ReadExactAsync(stream, 2, cancellationToken);
        if (header is null)
        {
            return null;
        }

        var opcode = (byte)(header[0] & 0x0F);
        var masked = (header[1] & 0x80) != 0;
        var payloadLen = (long)(header[1] & 0x7F);

        if (payloadLen == 126)
        {
            var ext = await ReadExactAsync(stream, 2, cancellationToken);
            if (ext is null)
            {
                return null;
            }

            payloadLen = (ext[0] << 8) | ext[1];
        }
        else if (payloadLen == 127)
        {
            var ext = await ReadExactAsync(stream, 8, cancellationToken);
            if (ext is null)
            {
                return null;
            }

            payloadLen = 0;
            for (var i = 0; i < 8; i++)
            {
                payloadLen = (payloadLen << 8) | ext[i];
            }
        }

        byte[]? maskKey = null;
        if (masked)
        {
            maskKey = await ReadExactAsync(stream, 4, cancellationToken);
            if (maskKey is null)
            {
                return null;
            }
        }

        var payload = new byte[payloadLen];
        if (payloadLen > 0)
        {
            var read = await ReadExactAsync(stream, (int)payloadLen, cancellationToken);
            if (read is null)
            {
                return null;
            }

            Array.Copy(read, payload, (int)payloadLen);
        }

        if (maskKey is not null)
        {
            for (var i = 0; i < payloadLen; i++)
            {
                payload[i] ^= maskKey[i % 4];
            }
        }

        return new Frame(opcode, payload);
    }

    private static async Task<byte[]?> ReadExactAsync(
        NetworkStream stream,
        int count,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken);
            if (n == 0)
            {
                return null;
            }

            read += n;
        }

        return buffer;
    }

    private static async Task<Dictionary<string, string>> ReadHttpHeadersAsync(
        NetworkStream stream,
        CancellationToken cancellationToken)
    {
        var headerBytes = new List<byte>();
        var buffer = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(buffer, cancellationToken);
            if (n == 0)
            {
                break;
            }

            headerBytes.Add(buffer[0]);
            if (headerBytes.Count >= 4)
            {
                var len = headerBytes.Count;
                if (headerBytes[len - 4] == (byte)'\r' && headerBytes[len - 3] == (byte)'\n' &&
                    headerBytes[len - 2] == (byte)'\r' && headerBytes[len - 1] == (byte)'\n')
                {
                    break;
                }
            }
        }

        var headerText = Encoding.UTF8.GetString(headerBytes.ToArray());
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines.Skip(1)) // 跳过请求行
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            headers[name] = value;
        }

        return headers;
    }

    private static async Task Send101ResponseAsync(
        NetworkStream stream,
        string acceptKey,
        CancellationToken cancellationToken)
    {
        var response =
            $"HTTP/1.1 101 Switching Protocols\r\n" +
            $"Upgrade: websocket\r\n" +
            $"Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {acceptKey}\r\n" +
            $"\r\n";
        var bytes = Encoding.UTF8.GetBytes(response);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static async Task Send401ResponseAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var response =
            "HTTP/1.1 401 Unauthorized\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        var bytes = Encoding.UTF8.GetBytes(response);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static async Task Send400ResponseAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var response =
            "HTTP/1.1 400 Bad Request\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n" +
            "\r\n";
        var bytes = Encoding.UTF8.GetBytes(response);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    private static async Task SendPongAsync(
        NetworkStream stream,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        var frame = BuildFrame(PongFrameOpcode, payload);
        await stream.WriteAsync(frame, cancellationToken);
    }

    private static async Task SendCloseAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var frame = BuildFrame(CloseFrameOpcode, Array.Empty<byte>());
        await stream.WriteAsync(frame, cancellationToken);
    }

    private static byte[] EncodeTextFrame(string message)
    {
        return BuildFrame(TextFrameOpcode, Encoding.UTF8.GetBytes(message));
    }

    private static byte[] BuildFrame(byte opcode, byte[] payload)
    {
        using var ms = new MemoryStream();
        ms.WriteByte((byte)(0x80 | opcode)); // FIN + opcode

        if (payload.Length <= 125)
        {
            ms.WriteByte((byte)payload.Length);
        }
        else if (payload.Length <= 65535)
        {
            ms.WriteByte(126);
            ms.WriteByte((byte)(payload.Length >> 8));
            ms.WriteByte((byte)payload.Length);
        }
        else
        {
            ms.WriteByte(127);
            var len = payload.Length;
            for (var i = 7; i >= 0; i--)
            {
                ms.WriteByte((byte)(len >> (i * 8)));
            }
        }

        ms.Write(payload, 0, payload.Length);
        return ms.ToArray();
    }

    private static string ComputeAcceptKey(string secWebSocketKey)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(secWebSocketKey + WebSocketGuid));
        return Convert.ToBase64String(hash);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _cts.Cancel();
        _listener.Stop();

        if (_acceptLoopTask is not null)
        {
            try
            {
                await _acceptLoopTask;
            }
            catch
            {
                // 接受循环被取消时可能抛异常，忽略。
            }
        }

        List<TcpClient> clients;
        lock (_clientsLock)
        {
            clients = _connectedClients.ToList();
            _connectedClients.Clear();
        }

        foreach (var client in clients)
        {
            try
            {
                client.Close();
            }
            catch
            {
                // 忽略关闭时的异常。
            }
        }

        _cts.Dispose();
    }

    private readonly record struct Frame(byte Opcode, byte[] Payload);
}
