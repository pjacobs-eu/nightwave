namespace Nightwave.Tuners.Dvb.Mpeg;

/// <summary>
/// Puts PSI and SI sections back together from the packets of one pid (ISO/IEC 13818-1,
/// 2.4.4). A section may start anywhere in a packet that has the payload unit start flag - the
/// pointer field says where - span several packets, and share a packet with the end of the one
/// before and the start of the one after.
/// </summary>
/// <remarks>
/// Sections that carry a CRC are only handed out when it checks, so a table read from a weak
/// signal is dropped rather than half believed. A packet that goes missing (a gap in the
/// continuity counter) throws away the section it was part of for the same reason.
/// </remarks>
internal sealed class SectionAssembler
{
    /// <summary>The largest section EN 300 468 allows (an EIT or a private section), header included.</summary>
    private const int MaximumSectionLength = 4096 + 3;

    private readonly byte[] _buffer = new byte[MaximumSectionLength + TransportPacket.Size];
    private int _length;
    private bool _collecting;
    private int _lastContinuity = -1;

    /// <summary>Takes one packet of this pid and calls <paramref name="onSection"/> for every section it completes.</summary>
    public void Push(ReadOnlySpan<byte> packet, Action<ReadOnlySpan<byte>> onSection)
    {
        ArgumentNullException.ThrowIfNull(onSection);

        if (packet.Length < TransportPacket.Size || packet[0] != TransportPacket.SyncByte)
        {
            return;
        }

        if (TransportPacket.HasTransportError(packet))
        {
            Reset();
            return;
        }

        if (!TransportPacket.HasPayload(packet))
        {
            return;
        }

        var continuity = TransportPacket.ContinuityCounter(packet);

        if (_lastContinuity >= 0)
        {
            if (continuity == _lastContinuity)
            {
                // Sent twice on purpose (13818-1 allows one duplicate); the first copy counted.
                return;
            }

            if (continuity != ((_lastContinuity + 1) & 0x0F))
            {
                Reset();
            }
        }

        _lastContinuity = continuity;

        var offset = TransportPacket.PayloadOffset(packet);
        if (offset >= TransportPacket.Size)
        {
            return;
        }

        var payload = packet[offset..TransportPacket.Size];

        if (TransportPacket.IsPayloadUnitStart(packet))
        {
            var pointer = payload[0];
            if (1 + pointer > payload.Length)
            {
                Reset();
                return;
            }

            // The bytes before the pointer target finish the section already under way.
            if (_collecting)
            {
                Append(payload.Slice(1, pointer));
                Drain(onSection);
            }

            _length = 0;
            _collecting = true;
            Append(payload[(1 + pointer)..]);
            Drain(onSection);
        }
        else if (_collecting)
        {
            Append(payload);
            Drain(onSection);
        }
    }

    public void Reset()
    {
        _length = 0;
        _collecting = false;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_length + data.Length > _buffer.Length)
        {
            // Longer than any section can be: whatever this is, it is not a table.
            Reset();
            return;
        }

        data.CopyTo(_buffer.AsSpan(_length));
        _length += data.Length;
    }

    /// <summary>Hands out every complete section at the front of the buffer, back to back.</summary>
    private void Drain(Action<ReadOnlySpan<byte>> onSection)
    {
        while (_collecting && _length >= 3)
        {
            if (_buffer[0] == 0xFF)
            {
                // Stuffing: nothing more starts in this packet.
                Reset();
                return;
            }

            var total = 3 + (((_buffer[1] & 0x0F) << 8) | _buffer[2]);
            if (total > MaximumSectionLength)
            {
                Reset();
                return;
            }

            if (_length < total)
            {
                return;
            }

            var section = _buffer.AsSpan(0, total);
            var hasCrc = (section[1] & 0x80) != 0;

            if (!hasCrc || MpegCrc32.IsValid(section))
            {
                onSection(section);
            }

            _length -= total;
            _buffer.AsSpan(total, _length).CopyTo(_buffer);
        }
    }
}
