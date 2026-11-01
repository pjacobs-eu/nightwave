using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Text;

namespace Nightwave.Tuners.Dvb.Transport;

/// <summary>
/// An RTSP control connection (RFC 2326). With interleaved transport the same TCP connection
/// also carries the stream, framed as <c>$</c>, channel, length, data (RFC 2326, 10.12) - so one
/// loop reads everything and sorts responses from data, and requests wait for their response
/// by sequence number.
/// </summary>
internal sealed class RtspConnection : IAsyncDisposable
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);

    private readonly Stream _stream;
    private readonly IDisposable? _owner;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<RtspResponse>> _pending = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[] _buffer = new byte[128 * 1024];
    private int _start;
    private int _end;
    private int _sequence;
    private Task? _reader;

    public RtspConnection(Stream stream, IDisposable? owner = null)
    {
        _stream = stream;
        _owner = owner;
    }

    /// <summary>Called with each frame on interleaved channel 0, the RTP of the stream. From the reading loop.</summary>
    public Action<ReadOnlySpan<byte>>? OnData { get; set; }

    /// <summary>Called with each frame on interleaved channel 1, the RTCP of the stream: the tuner's signal reports. From the reading loop.</summary>
    public Action<ReadOnlySpan<byte>>? OnControl { get; set; }

    /// <summary>Called once when the connection ends without having been closed here.</summary>
    public Action<Exception>? OnClosed { get; set; }

    /// <summary>Why the connection ended, once it has.</summary>
    public Exception? Failure { get; private set; }

    public static async Task<RtspConnection> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true, ReceiveBufferSize = 1024 * 1024 };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            client.Dispose();
            throw new TunerTransportException($"RTSP connection to {host}:{port} failed: {error.Message}", error)
            {
                Unreachable = true,
            };
        }

        var connection = new RtspConnection(client.GetStream(), client);
        connection.Start();

        return connection;
    }

    public void Start() => _reader ??= Task.Run(ReadLoopAsync);

    /// <summary>Sends a request and waits for its response. Requests go one at a time.</summary>
    public async Task<RtspResponse> SendAsync(
        string method,
        string uri,
        IEnumerable<KeyValuePair<string, string>> headers,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(headers);

        if (Failure is { } failure)
        {
            throw new TunerTransportException($"The RTSP connection is closed: {failure.Message}", failure);
        }

        await _send.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sequence = ++_sequence;
            var completion = new TaskCompletionSource<RtspResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[sequence] = completion;

            var request = new StringBuilder()
                .Append(CultureInfo.InvariantCulture, $"{method} {uri} RTSP/1.0\r\n")
                .Append(CultureInfo.InvariantCulture, $"CSeq: {sequence}\r\n")
                .Append("User-Agent: Nightwave\r\n");

            foreach (var (name, value) in headers)
            {
                request.Append(CultureInfo.InvariantCulture, $"{name}: {value}\r\n");
            }

            request.Append("\r\n");

            try
            {
                await _stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()), cancellationToken).ConfigureAwait(false);
                await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException error)
            {
                _pending.TryRemove(sequence, out _);
                throw new TunerTransportException($"The RTSP {method} could not be sent: {error.Message}", error);
            }

            try
            {
                return await completion.Task.WaitAsync(ResponseTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException error)
            {
                throw new TunerTransportException(
                    $"The device did not answer the RTSP {method} within {ResponseTimeout.TotalSeconds:0} seconds.",
                    error);
            }
            finally
            {
                _pending.TryRemove(sequence, out _);
            }
        }
        finally
        {
            _send.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _stream.DisposeAsync().ConfigureAwait(false);
        _owner?.Dispose();

        if (_reader is not null)
        {
            try
            {
                await _reader.ConfigureAwait(false);
            }
            catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // Closed on purpose.
            }
        }

        _stop.Dispose();
        _send.Dispose();
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await EnsureAsync(1).ConfigureAwait(false);

                if (_buffer[_start] == (byte)'$')
                {
                    await EnsureAsync(4).ConfigureAwait(false);

                    var channel = _buffer[_start + 1];
                    var length = (_buffer[_start + 2] << 8) | _buffer[_start + 3];

                    await EnsureAsync(4 + length).ConfigureAwait(false);

                    if (channel == 0)
                    {
                        OnData?.Invoke(_buffer.AsSpan(_start + 4, length));
                    }
                    else if (channel == 1)
                    {
                        OnControl?.Invoke(_buffer.AsSpan(_start + 4, length));
                    }

                    _start += 4 + length;
                }
                else
                {
                    await ReadResponseAsync().ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException
            or TunerTransportException or SocketException)
        {
            Failure = _stop.IsCancellationRequested ? new TunerTransportException("The RTSP connection was closed.") : error;

            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(new TunerTransportException($"The RTSP connection ended: {Failure.Message}", Failure));
            }

            if (!_stop.IsCancellationRequested)
            {
                OnClosed?.Invoke(Failure);
            }
        }
    }

    private async Task ReadResponseAsync()
    {
        var head = new StringBuilder();

        while (true)
        {
            var line = await ReadLineAsync().ConfigureAwait(false);
            if (line.Length == 0)
            {
                if (head.Length == 0)
                {
                    // Blank lines between messages are allowed and mean nothing.
                    return;
                }

                break;
            }

            head.Append(line).Append("\r\n");
        }

        var response = RtspResponse.Parse(head.ToString())
            ?? throw new TunerTransportException($"The device sent something that is not RTSP: '{head.ToString().Trim()}'.");

        if (response.ContentLength is > 0 and var length && length <= _buffer.Length)
        {
            await EnsureAsync(length).ConfigureAwait(false);
            response = response with { Body = Encoding.UTF8.GetString(_buffer, _start, length) };
            _start += length;
        }

        if (response.CSeq is { } sequence && _pending.TryGetValue(sequence, out var completion))
        {
            completion.TrySetResult(response);
        }
        else if (response.CSeq is null && _pending.Values.FirstOrDefault() is { } only)
        {
            // A server that leaves out CSeq answers the one request that is waiting.
            only.TrySetResult(response);
        }
    }

    private async Task<string> ReadLineAsync()
    {
        while (true)
        {
            var span = _buffer.AsSpan(_start, _end - _start);
            var newline = span.IndexOf((byte)'\n');

            if (newline >= 0)
            {
                var line = Encoding.ASCII.GetString(span[..newline]).TrimEnd('\r');
                _start += newline + 1;

                return line;
            }

            if (_end - _start >= 8192)
            {
                throw new TunerTransportException("The device sent an RTSP header line longer than 8 KB.");
            }

            await EnsureAsync(_end - _start + 1).ConfigureAwait(false);
        }
    }

    /// <summary>Reads until at least <paramref name="count"/> unread bytes are buffered.</summary>
    private async Task EnsureAsync(int count)
    {
        if (count > _buffer.Length)
        {
            throw new TunerTransportException($"An interleaved frame of {count} bytes does not fit the receive buffer.");
        }

        while (_end - _start < count)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = 0;
            }
            else if (_start > 0 && (_buffer.Length - _start < count || _end == _buffer.Length))
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            var read = await _stream.ReadAsync(_buffer.AsMemory(_end), _stop.Token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("The device closed the RTSP connection.");
            }

            _end += read;
        }
    }
}
