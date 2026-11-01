using System.Threading.Channels;

namespace Nightwave.Core.Streaming;

/// <summary>
/// One connection to a web stream, shared by everybody watching that channel. A provider counts
/// connections, so two people on the same channel must not cost two of them.
/// </summary>
public sealed class SharedUpstream : IAsyncDisposable
{
    private const int ChunkSize = 188 * 348;

    /// <summary>How much a slow reader may fall behind before chunks are dropped for it alone.</summary>
    private const int ReaderCapacity = 16 * 1024 * 1024;

    /// <summary>How often a stream that had been running is connected again after it ended.</summary>
    private const int MaxReconnects = 3;

    private readonly Func<CancellationToken, Task<Stream>> _open;
    private readonly Lock _gate = new();
    private readonly List<Reader> _readers = [];
    private readonly CancellationTokenSource _stop = new();
    private Task? _pump;
    private TaskCompletionSource? _connected;
    private Exception? _endedBecause;
    private bool _ended;

    public SharedUpstream(Func<CancellationToken, Task<Stream>> open)
    {
        ArgumentNullException.ThrowIfNull(open);

        _open = open;
    }

    public long BytesRead => Interlocked.Read(ref _bytesRead);

    private long _bytesRead;

    /// <summary>
    /// A new reader. The first one starts the connection and waits for it, so a provider's refusal
    /// reaches whoever asked instead of an empty stream.
    /// </summary>
    public async Task<Stream> SubscribeAsync(CancellationToken cancellationToken)
    {
        var reader = new Reader(this);
        Task connected;

        lock (_gate)
        {
            if (_ended)
            {
                // Nothing will come any more, and a viewer who got here late has to hear that.
                reader.Complete(_endedBecause);
                connected = _connected?.Task ?? Task.CompletedTask;
            }
            else
            {
                _readers.Add(reader);

                if (_pump is null)
                {
                    _connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _pump = Task.Run(PumpAsync, CancellationToken.None);
                }

                connected = _connected!.Task;
            }
        }

        try
        {
            await connected.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            reader.Dispose();

            throw;
        }

        return reader;
    }

    /// <summary>Ends the connection for everybody on it, with <paramref name="reason"/> as what they read.</summary>
    public ValueTask EndAsync(Exception reason)
    {
        lock (_gate)
        {
            _endedBecause = reason;
        }

        return DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);

        Task? pump;

        lock (_gate)
        {
            pump = _pump;
            _ended |= pump is null;
        }

        if (pump is not null)
        {
            await pump.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _stop.Dispose();
    }

    private async Task PumpAsync()
    {
        var buffer = new byte[ChunkSize];
        var reconnects = 0;
        Exception? failure = null;

        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var upstream = await _open(_stop.Token).ConfigureAwait(false);

                await using (upstream.ConfigureAwait(false))
                {
                    _connected!.TrySetResult();

                    while (true)
                    {
                        var read = await upstream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);

                        if (read == 0)
                        {
                            break;
                        }

                        reconnects = 0;
                        Interlocked.Add(ref _bytesRead, read);
                        Distribute(buffer.AsSpan(0, read).ToArray());
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error) when (_connected!.Task.IsCompleted)
            {
                failure = error;
            }
            catch (Exception error)
            {
                // Never connected: whoever asked hears why, with the provider's own message.
                _connected!.TrySetException(error);
                failure = error;

                break;
            }

            if (++reconnects > MaxReconnects)
            {
                break;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(reconnects), _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        lock (_gate)
        {
            _ended = true;

            foreach (var reader in _readers)
            {
                reader.Complete(_endedBecause ?? failure);
            }

            // Stopped before it connected: whoever waits for that hears from their reader.
            _connected!.TrySetResult();
        }
    }

    private void Distribute(ReadOnlyMemory<byte> chunk)
    {
        lock (_gate)
        {
            foreach (var reader in _readers)
            {
                reader.Offer(chunk);
            }
        }
    }

    private void Remove(Reader reader)
    {
        lock (_gate)
        {
            _readers.Remove(reader);
        }
    }

    private sealed class Reader(SharedUpstream owner) : Stream
    {
        private readonly Channel<ReadOnlyMemory<byte>> _chunks = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
            new UnboundedChannelOptions { SingleReader = true });

        private ReadOnlyMemory<byte> _current;
        private long _queued;
        private int _closed;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void Offer(ReadOnlyMemory<byte> chunk)
        {
            if (Interlocked.Read(ref _queued) + chunk.Length <= ReaderCapacity)
            {
                Interlocked.Add(ref _queued, chunk.Length);
                _chunks.Writer.TryWrite(chunk);
            }
        }

        public void Complete(Exception? failure) => _chunks.Writer.TryComplete(failure);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current.IsEmpty)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }

                if (_chunks.Reader.TryRead(out var chunk))
                {
                    _current = chunk;
                    Interlocked.Add(ref _queued, -chunk.Length);
                }
            }

            var count = Math.Min(buffer.Length, _current.Length);

            _current[..count].CopyTo(buffer);
            _current = _current[count..];

            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _closed, 1) == 0)
            {
                owner.Remove(this);
                _chunks.Writer.TryComplete();
            }

            base.Dispose(disposing);
        }
    }
}
