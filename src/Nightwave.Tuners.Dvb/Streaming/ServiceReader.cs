using System.Threading.Channels;

namespace Nightwave.Tuners.Dvb.Streaming;

/// <summary>
/// One reader of a tuned service: what <see cref="Nightwave.Core.Tuning.ITunedStream.OpenReadAsync"/>
/// hands out. Fed by the tuner's loop, drained by whoever reads.
/// </summary>
/// <remarks>
/// The tuner is shared - other viewers, other channels on the same transponder - so a reader
/// that stops reading may not hold it up, and may not make it buffer without end either. Up to
/// <see cref="Capacity"/> bytes are kept; past that, new data for this reader is dropped whole
/// packets at a time, and the tuner moves on. A player sees a gap and resynchronises; everybody
/// else sees nothing.
/// </remarks>
internal sealed class ServiceReader : Stream
{
    /// <summary>A few seconds of a high bit rate HD service.</summary>
    public const int Capacity = 16 * 1024 * 1024;

    private readonly Channel<ReadOnlyMemory<byte>> _chunks = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly Action<ServiceReader> _onClosed;
    private readonly int _capacity;
    private ReadOnlyMemory<byte> _current;
    private long _queued;
    private long _dropped;
    private int _closed;

    public ServiceReader(Action<ServiceReader> onClosed, int capacity = Capacity)
    {
        _onClosed = onClosed;
        _capacity = capacity;
    }

    /// <summary>Bytes dropped because this reader was too slow, since last asked.</summary>
    public long TakeDropped() => Interlocked.Exchange(ref _dropped, 0);

    /// <summary>
    /// Whether this reader is still waiting for a picture it can start on. A broadcast is joined
    /// in the middle of one, and the slices of a picture whose beginning nobody saw are what a
    /// decoder cannot use and complains about until the next whole one. Set and cleared by the
    /// feed under its own lock, and touched from nowhere else.
    /// </summary>
    public bool WaitsForPicture { get; set; }

    /// <summary>When it is handed the stream as it is regardless. See <see cref="WaitsForPicture"/>.</summary>
    public DateTimeOffset PictureWaitUntil { get; set; }

    /// <summary>
    /// Queues data for this reader. Never blocks; returns false when the data was dropped
    /// because the reader is too far behind.
    /// </summary>
    public bool Offer(ReadOnlyMemory<byte> data)
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            return true;
        }

        if (Interlocked.Read(ref _queued) + data.Length > _capacity)
        {
            Interlocked.Add(ref _dropped, data.Length);
            return false;
        }

        Interlocked.Add(ref _queued, data.Length);
        _chunks.Writer.TryWrite(data);

        return true;
    }

    /// <summary>Ends what this reader reads: the end of the stream, or the error that ended it.</summary>
    public void Complete(Exception? error = null) => _chunks.Writer.TryComplete(error);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_current.IsEmpty)
        {
            try
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }
            }
            catch (ChannelClosedException closed) when (closed.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(closed.InnerException).Throw();
            }

            if (_chunks.Reader.TryRead(out var next))
            {
                _current = next;
                Interlocked.Add(ref _queued, -next.Length);
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
            _chunks.Writer.TryComplete();
            _onClosed(this);
        }

        base.Dispose(disposing);
    }
}
