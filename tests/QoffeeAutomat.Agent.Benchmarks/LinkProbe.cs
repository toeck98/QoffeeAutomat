using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using QoffeeAutomat.Contracts.Link;
using QoffeeAutomat.Core.Security;

namespace QoffeeAutomat.Agent.Benchmarks;

/// <summary>
/// One QoffeeLink client over a real WebSocket connection (TLS, bearer token, certificate pinned by the fingerprint
/// from pairing). It subscribes to metrics and counts the state frames and their size; the first state frame after the
/// subscription is the full snapshot and is kept apart, because the budget is about the frames of steady operation.
/// </summary>
internal sealed class LinkProbe : IAsyncDisposable
{
    private const int SubscribeId = 1;
    private const int BufferSize = 16 * 1024;

    private readonly ClientWebSocket _socket;
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _subscribeFrame;
    private Task _reader = Task.CompletedTask;
    private long _stateFrames;
    private long _stateBytes;
    private int _snapshotBytes = -1;
    private string? _closeReason;

    private LinkProbe(ClientWebSocket socket, IReadOnlyList<string> metrics)
    {
        _socket = socket;
        string list = string.Join(",", metrics.Select(metric => JsonSerializer.Serialize(metric)));
        _subscribeFrame = "{\"t\":\"" + MessageTypes.Subscribe + "\",\"id\":" + SubscribeId.ToString(CultureInfo.InvariantCulture) + ",\"metrics\":[" + list + "]}";
    }

    /// <summary>The size of the full snapshot, or -1 while it has not arrived.</summary>
    public int SnapshotBytes => Volatile.Read(ref _snapshotBytes);

    /// <summary>True while the reader runs, that is while the agent has not ended the connection.</summary>
    public bool IsConnected => !_reader.IsCompleted;

    /// <summary>Why the connection ended, or null while it is up.</summary>
    public string? CloseReason => Volatile.Read(ref _closeReason);

    /// <summary>The state frames after the snapshot and their size in bytes, as counted so far.</summary>
    public (long Frames, long Bytes) Counters => (Interlocked.Read(ref _stateFrames), Interlocked.Read(ref _stateBytes));

    /// <summary>Connects, answers <c>hello</c>, subscribes and waits for the snapshot.</summary>
    public static async Task<LinkProbe> ConnectAsync(
        int port,
        string token,
        string fingerprint,
        IReadOnlyList<string> metrics,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(ProtocolInfo.SubProtocol);
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

        // The certificate of the agent is self-signed; the client trusts exactly the one whose fingerprint pairing returned.
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, _) => IsPinned(certificate, fingerprint);

        var probe = new LinkProbe(socket, metrics);
        try
        {
            await socket.ConnectAsync(new Uri("wss://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + ProtocolInfo.LinkPath), cancellationToken);
            probe._reader = Task.Run(probe.ReadLoopAsync, CancellationToken.None);
            await probe._subscribed.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            wait.CancelAfter(TimeSpan.FromSeconds(30));
            while (probe.SnapshotBytes < 0)
            {
                await Task.Delay(20, wait.Token);
            }

            return probe;
        }
        catch
        {
            await probe.DisposeAsync();
            throw;
        }
    }

    private static bool IsPinned(X509Certificate? certificate, string fingerprint)
    {
        if (certificate is null)
        {
            return false;
        }

        using var copy = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        return CertificateFingerprint.AreEqual(CertificateFingerprint.Compute(copy), fingerprint);
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
                await _reader.WaitAsync(timeout.Token);
            }
            catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The agent closed first or did not answer; the socket is disposed below anyway.
            }
        }

        await _stop.CancelAsync();
        _socket.Dispose();
        _stop.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        byte[] buffer = new byte[BufferSize];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer.AsMemory(), _stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        Volatile.Write(ref _closeReason, "the agent closed the connection: " + _socket.CloseStatus + " " + _socket.CloseStatusDescription);
                        if (_socket.State == WebSocketState.CloseReceived)
                        {
                            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        }

                        _subscribed.TrySetException(new InvalidOperationException(CloseReason));
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                await HandleAsync(message.GetBuffer().AsMemory(0, (int)message.Length));
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException or JsonException)
        {
            Volatile.Write(ref _closeReason, "the connection failed: " + exception.Message);
            _subscribed.TrySetException(exception);
        }
    }

    private async Task HandleAsync(ReadOnlyMemory<byte> frame)
    {
        using JsonDocument document = JsonDocument.Parse(frame);
        string type = document.RootElement.TryGetProperty("t", out JsonElement t) ? t.GetString() ?? string.Empty : string.Empty;
        switch (type)
        {
            case MessageTypes.Hello:
                await SendAsync("{\"t\":\"client_hello\",\"protocol\":" + ProtocolInfo.Version.ToString(CultureInfo.InvariantCulture)
                    + ",\"client\":{\"kind\":\"tool\",\"name\":\"Benchmark\",\"version\":\"1.0.0\"}}");
                await SendAsync(_subscribeFrame);
                break;
            case MessageTypes.Result:
                if (document.RootElement.TryGetProperty("ok", out JsonElement ok) && ok.ValueKind == JsonValueKind.True)
                {
                    _subscribed.TrySetResult();
                }
                else
                {
                    _subscribed.TrySetException(new InvalidOperationException("The agent refused the subscription: " + Encoding.UTF8.GetString(frame.Span)));
                }

                break;
            case MessageTypes.State:
                if (Volatile.Read(ref _snapshotBytes) < 0)
                {
                    Volatile.Write(ref _snapshotBytes, frame.Length);
                }
                else
                {
                    Interlocked.Increment(ref _stateFrames);
                    Interlocked.Add(ref _stateBytes, frame.Length);
                }

                break;
            default:
                break;
        }
    }

    private async Task SendAsync(string json)
    {
        await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, _stop.Token);
    }
}
