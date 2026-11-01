using System.Buffers;

namespace Nightwave.Core.Streaming;

/// <summary>A live stream read through a <see cref="TsRepairFilter"/>.</summary>
public sealed class RepairingStream : Stream
{
    private const int ChunkSize = TsRepairFilter.PacketSize * 512;

    private readonly Stream _inner;
    private readonly byte[] _in = ArrayPool<byte>.Shared.Rent(ChunkSize);
    private readonly byte[] _out = ArrayPool<byte>.Shared.Rent(ChunkSize + TsRepairFilter.PacketSize);
    private int _available;
    private int _offset;
    private bool _disposed;

    public RepairingStream(Stream inner, TsRepairFilter filter)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(filter);

        _inner = inner;
        Filter = filter;
    }

    public TsRepairFilter Filter { get; }

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
        while (_offset >= _available)
        {
            var read = await _inner.ReadAsync(_in.AsMemory(0, ChunkSize), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                return 0;
            }

            _available = Filter.Process(_in.AsSpan(0, read), _out);
            _offset = 0;
        }

        var count = Math.Min(buffer.Length, _available - _offset);

        _out.AsMemory(_offset, count).CopyTo(buffer);
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
        if (disposing && !_disposed)
        {
            _disposed = true;
            _inner.Dispose();
            ArrayPool<byte>.Shared.Return(_in);
            ArrayPool<byte>.Shared.Return(_out);
        }

        base.Dispose(disposing);
    }
}
