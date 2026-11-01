using System.Diagnostics;
using System.Threading.Channels;

namespace Nightwave.Core.Streaming;

/// <summary>
/// A live stream read through a <see cref="StreamBridge"/>: what the source delivers is passed on
/// at once, and where a picture stays away the bridge fills in, so that a player never runs dry.
/// </summary>
public sealed class BridgingStream : Stream
{
    private const int ChunkSize = StreamBridge.PacketSize * 513;

    /// <summary>How often the bridge is asked whether it has to step in.</summary>
    private static readonly TimeSpan Check = TimeSpan.FromMilliseconds(50);

    private readonly Stream _inner;
    private readonly StreamBridge _bridge;
    private readonly Channel<(byte[] Data, long ArrivedAt)> _chunks = Channel.CreateBounded<(byte[] Data, long ArrivedAt)>(new BoundedChannelOptions(64) { SingleReader = true, SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly List<byte> _fill = [];
    private readonly long _startedAt = Stopwatch.GetTimestamp();
    private readonly Task _pump;
    private byte[] _current = [];
    private bool _fillDue;
    private int _offset;

    public BridgingStream(Stream inner, StreamBridge bridge)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(bridge);

        _inner = inner;
        _bridge = bridge;
        _pump = Task.Run(PumpAsync, CancellationToken.None);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    private long NowMs => (long)Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (_offset >= _current.Length)
        {
            // What already arrived comes first, judged by when it arrived rather than when it is
            // read: a reader that stalls for a moment finds pictures waiting, and the bridge must
            // not take that for an outage. Audio keeps arriving while the repair holds a picture
            // back, so the bridge is asked after every chunk as well as when nothing is there.
            if (_chunks.Reader.TryRead(out var chunk))
            {
                _bridge.Pass(chunk.Data, chunk.ArrivedAt);
                _fillDue = _bridge.ShouldFill(chunk.ArrivedAt);
                _current = chunk.Data;
                _offset = 0;

                continue;
            }

            if (_fillDue || _bridge.ShouldFill(NowMs))
            {
                _fillDue = false;
                _fill.Clear();
                _bridge.Fill(_fill, NowMs);
                _current = [.. _fill];
                _offset = 0;

                continue;
            }

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            wait.CancelAfter(Check);

            try
            {
                if (!await _chunks.Reader.WaitToReadAsync(wait.Token).ConfigureAwait(false))
                {
                    return 0;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Nothing came; time to look at the bridge again.
            }
        }

        var count = Math.Min(buffer.Length, _current.Length - _offset);

        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;

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
        if (disposing)
        {
            _stop.Cancel();
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task PumpAsync()
    {
        var buffer = new byte[ChunkSize];
        Exception? failure = null;

        try
        {
            while (true)
            {
                var read = await _inner.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                await _chunks.Writer.WriteAsync((buffer[..read], NowMs), _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Closed.
        }
        catch (Exception error) when (error is not ObjectDisposedException)
        {
            failure = error;
        }
        catch (ObjectDisposedException)
        {
            // Closed.
        }

        _chunks.Writer.TryComplete(failure);
    }
}
