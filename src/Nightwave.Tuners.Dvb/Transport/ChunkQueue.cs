using System.Threading.Channels;

namespace Nightwave.Tuners.Dvb.Transport;

/// <summary>
/// Hands pieces of a stream from the loop that receives them to the one that reads them.
/// Bounded: when the reader falls behind by more than the queue holds, new pieces are dropped
/// and counted rather than kept - a tuner delivers in real time whether anybody keeps up or not.
/// </summary>
internal sealed class ChunkQueue
{
    private readonly Channel<byte[]> _chunks;
    private byte[]? _current;
    private int _offset;
    private long _dropped;

    public ChunkQueue(int capacity)
    {
        _chunks = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });
    }

    /// <summary>How many pieces were dropped since the last time this was asked.</summary>
    public long TakeDropped() => Interlocked.Exchange(ref _dropped, 0);

    public void Write(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return;
        }

        if (!_chunks.Writer.TryWrite(data.ToArray()))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Ends the stream: the reader sees zero, or the exception.</summary>
    public void Complete(Exception? error = null) => _chunks.Writer.TryComplete(error);

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (_current is null || _offset >= _current.Length)
        {
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            if (_chunks.Reader.TryRead(out var next))
            {
                _current = next;
                _offset = 0;
            }
        }

        // As much as is queued, not one datagram per call: the reader cuts packets and batches
        // them, and a call per 1316 bytes would be most of its work.
        var written = 0;

        while (written < buffer.Length)
        {
            if (_current is null || _offset >= _current.Length)
            {
                if (!_chunks.Reader.TryRead(out var next))
                {
                    break;
                }

                _current = next;
                _offset = 0;
            }

            var count = Math.Min(buffer.Length - written, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer[written..]);
            _offset += count;
            written += count;
        }

        return written;
    }
}
