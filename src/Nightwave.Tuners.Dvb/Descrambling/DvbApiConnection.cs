using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Nightwave.Tuners.Dvb.Descrambling;

/// <summary>
/// One TCP connection to the descrambling server's dvbapi port, from the handshake until it
/// drops: a reader that decodes what the server sends and hands it on, and a writer that sends
/// what is queued.
/// </summary>
/// <remarks>
/// Writing never blocks the caller: messages go through a bounded queue, so the stream thread
/// that forwards sections does not wait for the network. When the queue is full the message is
/// dropped and counted; that only happens when the server stops reading.
/// </remarks>
internal sealed partial class DvbApiConnection : IAsyncDisposable
{
    private const int QueueCapacity = 4096;

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly Channel<Func<uint, byte[]>> _outgoing;
    private readonly CancellationTokenSource _stop = new();
    private readonly ILogger _logger;
    private readonly Action<DvbApiServerMessage> _onMessage;
    private readonly TaskCompletionSource<string> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly byte[] _receiveBuffer;
    private int _received;
    private int _messageId;
    private long _dropped;
    private Task _reader = Task.CompletedTask;
    private Task _writer = Task.CompletedTask;

    private DvbApiConnection(
        Socket socket,
        string endpoint,
        int protocolVersion,
        Action<DvbApiServerMessage> onMessage,
        ILogger logger)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: true);
        Endpoint = endpoint;
        ProtocolVersion = protocolVersion;
        _onMessage = onMessage;
        _logger = logger;
        _receiveBuffer = new byte[64 * 1024];
        _outgoing = Channel.CreateBounded<Func<uint, byte[]>>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public string Endpoint { get; }

    public int ProtocolVersion { get; }

    public string ServerVersion { get; private set; } = string.Empty;

    /// <summary>The protocol version the server says it speaks.</summary>
    public int ServerProtocolVersion { get; private set; }

    /// <summary>Completes with the reason once the connection is gone, whichever side ended it.</summary>
    public Task<string> Closed => _closed.Task;

    public bool IsAlive => !_closed.Task.IsCompleted;

    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// Connects, greets and waits for the server's answer. Fails with a
    /// <see cref="Nightwave.Core.Tuning.TunerException"/> naming the endpoint.
    /// </summary>
    /// <remarks>
    /// The greeting is sent plain first, as the protocol says. The server (the reference
    /// implementation, checked at commit 5905109e) reads what arrives with a new connection
    /// using the protocol version of the last client that greeted on its listening socket; after
    /// a version 3 client that is 3, and it then takes the plain greeting for a malformed
    /// version 3 message and stays silent. If there is no answer in
    /// <see cref="DvbApiTimings.PlainGreetingTimeout"/>, the greeting is repeated on a new
    /// connection with the version 3 prefix, which that state does read - and which puts the
    /// server back to this client's version for whoever connects next.
    /// </remarks>
    public static async Task<DvbApiConnection> OpenAsync(
        DvbApiOptions options,
        DvbApiTimings timings,
        Action<DvbApiServerMessage> onMessage,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            return await OpenAsync(options, timings, onMessage, logger, framedGreeting: false, timings.PlainGreetingTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GreetingUnansweredException)
        {
            LogGreetingRetry(logger, options.Host, options.Port, timings.PlainGreetingTimeout.TotalSeconds);
        }

        try
        {
            return await OpenAsync(options, timings, onMessage, logger, framedGreeting: true, timings.HandshakeTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (GreetingUnansweredException)
        {
            throw DescramblerErrors.HandshakeFailed(
                $"{options.Host}:{options.Port}",
                $"no answer to the greeting, neither plain within {timings.PlainGreetingTimeout.TotalSeconds:0.#} seconds "
                    + $"nor framed within {timings.HandshakeTimeout.TotalSeconds:0.#} seconds");
        }
    }

    private static async Task<DvbApiConnection> OpenAsync(
        DvbApiOptions options,
        DvbApiTimings timings,
        Action<DvbApiServerMessage> onMessage,
        ILogger logger,
        bool framedGreeting,
        TimeSpan greetingTimeout,
        CancellationToken cancellationToken)
    {
        var endpoint = $"{options.Host}:{options.Port}";
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        var handedOver = false;
        try
        {
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectTimeout.CancelAfter(timings.ConnectTimeout);
                try
                {
                    await socket.ConnectAsync(options.Host, options.Port, connectTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw DescramblerErrors.Unreachable(
                        endpoint,
                        $"no connection within {timings.ConnectTimeout.TotalSeconds:0} seconds");
                }
                catch (SocketException exception)
                {
                    throw DescramblerErrors.Unreachable(endpoint, $"{exception.SocketErrorCode}: {exception.Message}");
                }
            }

            var connection = new DvbApiConnection(socket, endpoint, timings.ProtocolVersion, onMessage, logger);
            handedOver = true;
            try
            {
                await connection.HandshakeAsync(options.ClientName, framedGreeting, greetingTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            connection._reader = Task.Run(connection.ReadLoopAsync, CancellationToken.None);
            connection._writer = Task.Run(connection.WriteLoopAsync, CancellationToken.None);
            return connection;
        }
        finally
        {
            if (!handedOver)
            {
                socket.Dispose();
            }
        }
    }

    /// <summary>
    /// Queues a message. The builder gets the message id (version 3) and runs on the writer, so
    /// ids go out in order. False when the connection is gone or the queue is full.
    /// </summary>
    public bool Send(Func<uint, byte[]> build)
    {
        if (!IsAlive)
        {
            return false;
        }

        if (_outgoing.Writer.TryWrite(build))
        {
            return true;
        }

        if (Interlocked.Increment(ref _dropped) % 1000 == 1)
        {
            LogQueueFull(Endpoint, Dropped);
        }

        return false;
    }

    /// <summary>Sends what is queued, within the timeout, and closes.</summary>
    public async Task CloseAsync(TimeSpan flushTimeout)
    {
        _outgoing.Writer.TryComplete();

        // The timer goes with the wait: closing the last service of a busy server must not leave
        // one running for the whole timeout.
        using var flush = new CancellationTokenSource();
        await Task.WhenAny(_writer, Task.Delay(flushTimeout, flush.Token)).ConfigureAwait(false);
        await flush.CancelAsync().ConfigureAwait(false);

        Terminate("closed by Nightwave");
    }

    public async ValueTask DisposeAsync()
    {
        Terminate("closed by Nightwave");
        try
        {
            await Task.WhenAll(_reader, _writer).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            // The loops end by their socket going away; how exactly is of no interest here.
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private void Terminate(string reason)
    {
        if (_closed.TrySetResult(reason))
        {
            _outgoing.Writer.TryComplete();
            try
            {
                _stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // Already disposed: nothing left to stop.
            }

            _socket.Dispose();
        }
    }

    private async Task HandshakeAsync(string clientName, bool framed, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeTimeout.CancelAfter(timeout);
        try
        {
            var greeting = DvbApiProtocol.EncodeClientInfo(ProtocolVersion, clientName, framed);
            await _stream.WriteAsync(greeting, handshakeTimeout.Token).ConfigureAwait(false);

            while (true)
            {
                var read = await _stream.ReadAsync(_receiveBuffer.AsMemory(_received), handshakeTimeout.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw DescramblerErrors.HandshakeFailed(Endpoint, "the connection was closed right after the greeting");
                }

                _received += read;
                if (DvbApiProtocol.TryDecode(_receiveBuffer.AsSpan(0, _received), ProtocolVersion, out var message, out var consumed))
                {
                    if (message is not DvbApiServerInfo info)
                    {
                        throw DescramblerErrors.HandshakeFailed(Endpoint, $"expected SERVER_INFO, got {message!.GetType().Name}");
                    }

                    ServerVersion = info.ServerVersion;
                    ServerProtocolVersion = info.ProtocolVersion;
                    Buffer.BlockCopy(_receiveBuffer, consumed, _receiveBuffer, 0, _received - consumed);
                    _received -= consumed;
                    LogConnected(Endpoint, ServerVersion, ServerProtocolVersion, ProtocolVersion);
                    return;
                }

                if (_received == _receiveBuffer.Length)
                {
                    throw DescramblerErrors.HandshakeFailed(Endpoint, "the answer to the greeting is not a dvbapi message");
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GreetingUnansweredException();
        }
        catch (DvbApiProtocolException exception)
        {
            throw DescramblerErrors.HandshakeFailed(Endpoint, exception.Message);
        }
        catch (IOException exception)
        {
            throw DescramblerErrors.HandshakeFailed(Endpoint, exception.Message);
        }
    }

    private async Task ReadLoopAsync()
    {
        var reason = "closed by the descrambling server";
        try
        {
            while (true)
            {
                // Whatever arrived with the handshake answer is decoded first.
                DecodeReceived();

                if (_received == _receiveBuffer.Length)
                {
                    throw new DvbApiProtocolException($"A single message is longer than {_receiveBuffer.Length} bytes.");
                }

                var read = await _stream.ReadAsync(_receiveBuffer.AsMemory(_received), _stop.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                _received += read;
            }
        }
        catch (OperationCanceledException)
        {
            reason = "closed by Nightwave";
        }
        catch (DvbApiProtocolException exception)
        {
            reason = $"protocol error: {exception.Message}";
            LogProtocolError(Endpoint, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException)
        {
            reason = IsAlive ? exception.Message : "closed by Nightwave";
        }

        Terminate(reason);
    }

    private void DecodeReceived()
    {
        var offset = 0;
        while (DvbApiProtocol.TryDecode(_receiveBuffer.AsSpan(offset, _received - offset), ProtocolVersion, out var message, out var consumed))
        {
            offset += consumed;
            try
            {
                _onMessage(message!);
            }
#pragma warning disable CA1031 // One service's failure must not take the connection of all the others with it.
            catch (Exception exception)
#pragma warning restore CA1031
            {
                LogHandlerFailed(exception, Endpoint, message!.GetType().Name);
            }
        }

        if (offset > 0)
        {
            Buffer.BlockCopy(_receiveBuffer, offset, _receiveBuffer, 0, _received - offset);
            _received -= offset;
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var build in _outgoing.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                var messageId = (uint)Interlocked.Increment(ref _messageId);
                await _stream.WriteAsync(build(messageId), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
#pragma warning disable CA1031 // A writer that stops without saying so would queue for ever.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            // Anything else - a message that cannot be encoded among it - ends the connection
            // rather than leaving it alive with nobody sending: the services then see it drop,
            // say so, and are reconnected.
            if (exception is not (IOException or SocketException or ObjectDisposedException))
            {
                LogWriterFailed(exception, Endpoint);
            }

            Terminate(exception.Message);
        }
    }

    /// <summary>No answer to the greeting in time; the caller decides what that means.</summary>
    private sealed class GreetingUnansweredException : Exception;

    [LoggerMessage(Level = LogLevel.Information, Message = "the descrambling server at {Host}:{Port} did not answer the plain greeting within {Seconds} s; greeting again with the protocol version 3 frame, which a server still set to the version of an earlier client reads")]
    private static partial void LogGreetingRetry(ILogger logger, string host, int port, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connected to the descrambling server at {Endpoint}: {ServerVersion}, protocol {ServerProtocol} (using {ClientProtocol})")]
    private partial void LogConnected(string endpoint, string serverVersion, int serverProtocol, int clientProtocol);

    [LoggerMessage(Level = LogLevel.Warning, Message = "the descrambling server at {Endpoint} sent something that is not the dvbapi protocol, dropping the connection: {Detail}")]
    private partial void LogProtocolError(string endpoint, string detail);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The queue to the descrambling server at {Endpoint} is full: the descrambling server is not reading. {Dropped} messages dropped so far")]
    private partial void LogQueueFull(string endpoint, long dropped);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling {Message} from the descrambling server at {Endpoint} failed")]
    private partial void LogHandlerFailed(Exception exception, string endpoint, string message);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sending to the descrambling server at {Endpoint} failed for a reason the protocol does not account for; the connection is dropped and reopened")]
    private partial void LogWriterFailed(Exception exception, string endpoint);
}
