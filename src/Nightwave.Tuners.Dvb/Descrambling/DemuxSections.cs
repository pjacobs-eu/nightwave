namespace Nightwave.Tuners.Dvb.Descrambling;

/// <summary>
/// A section filter the descrambling server set with DMX_SET_FILTER, matched the way a Linux DVB
/// demux matches it, since that is what the server writes them for.
/// </summary>
/// <remarks>
/// Filter byte 0 is compared with the table id, filter bytes 1 to 15 with section bytes 3 to 17:
/// the demux skips the two length bytes. Bits set in the mask are compared. Of those, bits with
/// the mode bit clear must be equal; bits with the mode bit set are a negative match, where at
/// least one of them has to differ. The server leaves the mode at zero, so in practice every
/// masked bit has to be equal.
/// </remarks>
internal sealed class DemuxFilter
{
    private readonly byte[] _value;
    private readonly byte[] _positiveMask;
    private readonly byte[] _negativeMask;
    private readonly bool _hasNegative;

    public DemuxFilter(int demux, int filter, int pid, byte[] value, byte[] mask, byte[] mode, uint flags)
    {
        Demux = demux;
        Filter = filter;
        Pid = pid;
        Flags = flags;
        _value = new byte[DvbApiProtocol.FilterLength];
        _positiveMask = new byte[DvbApiProtocol.FilterLength];
        _negativeMask = new byte[DvbApiProtocol.FilterLength];
        for (var i = 0; i < DvbApiProtocol.FilterLength; i++)
        {
            _value[i] = i < value.Length ? value[i] : (byte)0;
            var m = i < mask.Length ? mask[i] : (byte)0;
            var negative = i < mode.Length ? mode[i] : (byte)0;
            _positiveMask[i] = (byte)(m & ~negative);
            _negativeMask[i] = (byte)(m & negative);
            _hasNegative |= _negativeMask[i] != 0;
        }
    }

    /// <summary>The descrambling server's demuxer number, repeated in FILTER_DATA.</summary>
    public int Demux { get; }

    public int Filter { get; }

    public int Pid { get; }

    public uint Flags { get; }

    public bool IsOneShot => (Flags & DvbApiProtocol.FilterFlagOneShot) != 0;

    public bool Matches(ReadOnlySpan<byte> section)
    {
        if ((Flags & DvbApiProtocol.FilterFlagCheckCrc) != 0 && !SectionCrc.IsValid(section))
        {
            return false;
        }

        var negativeDiffers = false;
        for (var i = 0; i < DvbApiProtocol.FilterLength; i++)
        {
            var positive = _positiveMask[i];
            var negative = _negativeMask[i];
            if ((positive | negative) == 0)
            {
                continue;
            }

            var offset = i == 0 ? 0 : i + 2;
            if (offset >= section.Length)
            {
                return false;
            }

            var difference = (byte)(section[offset] ^ _value[i]);
            if ((difference & positive) != 0)
            {
                return false;
            }

            negativeDiffers |= (difference & negative) != 0;
        }

        return !_hasNegative || negativeDiffers;
    }
}

/// <summary>
/// Puts the sections on one pid back together from its transport stream packets: sections that
/// span packets, several sections in one packet, stuffing after the last.
/// </summary>
/// <remarks>
/// A packet with payload_unit_start_indicator set begins with a pointer field: the bytes before
/// the pointer's target end the section in progress, a new section starts after it. A
/// continuity counter that skips drops the section in progress, since a piece of it is missing;
/// a repeated counter is a duplicate packet and is ignored. Scrambled packets and packets with
/// the transport error indicator carry no usable section data.
/// </remarks>
internal sealed class DemuxSectionAssembler
{
    /// <summary>The largest section a private table may have: 3 header bytes and a 12 bit length up to 4093.</summary>
    private const int MaxSection = 4096;

    private readonly byte[] _section = new byte[MaxSection];
    private int _collected;
    private bool _inProgress;
    private int _lastContinuity = -1;

    /// <summary>Feeds one 188 byte packet; <paramref name="onSection"/> gets every section it completes.</summary>
    public void Feed<TState>(ReadOnlySpan<byte> packet, TState state, SectionHandler<TState> onSection)
    {
        if (packet.Length < 188 || packet[0] != 0x47 || (packet[1] & 0x80) != 0 || (packet[3] & 0xC0) != 0)
        {
            Reset();
            return;
        }

        var adaptation = (packet[3] >> 4) & 0x3;
        if ((adaptation & 0x1) == 0)
        {
            return; // no payload, and no counter step
        }

        var continuity = packet[3] & 0x0F;
        if (continuity == _lastContinuity)
        {
            return;
        }

        if (_lastContinuity >= 0 && continuity != ((_lastContinuity + 1) & 0x0F))
        {
            Reset();
        }

        _lastContinuity = continuity;

        var start = 4;
        if (adaptation == 0x3)
        {
            start += 1 + packet[4];
        }

        if (start >= 188)
        {
            return;
        }

        var payload = packet[start..188];
        if ((packet[1] & 0x40) == 0)
        {
            if (_inProgress)
            {
                Append(payload, state, onSection);
            }

            return;
        }

        var pointer = payload[0];
        if (1 + pointer > payload.Length)
        {
            Reset();
            return;
        }

        if (_inProgress)
        {
            Append(payload.Slice(1, pointer), state, onSection);
            _inProgress = false;
        }

        var rest = payload[(1 + pointer)..];
        while (rest.Length > 0 && rest[0] != 0xFF)
        {
            _inProgress = true;
            _collected = 0;
            rest = Append(rest, state, onSection);
            if (_inProgress)
            {
                break;
            }
        }
    }

    private void Reset()
    {
        _inProgress = false;
        _collected = 0;
    }

    private int SectionTotal() => 3 + (((_section[1] & 0x0F) << 8) | _section[2]);

    /// <summary>Adds bytes to the section in progress; returns what is left after it completes.</summary>
    private ReadOnlySpan<byte> Append<TState>(ReadOnlySpan<byte> data, TState state, SectionHandler<TState> onSection)
    {
        while (data.Length > 0 && _inProgress)
        {
            var needed = _collected < 3 ? 3 : SectionTotal();
            var take = Math.Min(needed - _collected, data.Length);
            data[..take].CopyTo(_section.AsSpan(_collected));
            _collected += take;
            data = data[take..];

            if (_collected < 3)
            {
                continue;
            }

            var total = SectionTotal();
            if (total > MaxSection)
            {
                Reset();
                return [];
            }

            if (total == 3)
            {
                // A section without a body is not one anybody can use.
                Reset();
            }
            else if (_collected == total)
            {
                _inProgress = false;
                onSection(_section.AsSpan(0, total), state);
            }
        }

        return data;
    }
}

internal delegate void SectionHandler<in TState>(ReadOnlySpan<byte> section, TState state);

/// <summary>CRC-32/MPEG-2, which ends every section with section_syntax_indicator set.</summary>
internal static class SectionCrc
{
    private static readonly uint[] Table = BuildTable();

    public static bool IsValid(ReadOnlySpan<byte> section)
    {
        if (section.Length < 3 || (section[1] & 0x80) == 0)
        {
            return true;
        }

        return section.Length >= 7 && Compute(section) == 0;
    }

    /// <summary>The CRC over the whole span; 0 when the span ends with a correct CRC.</summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc = (crc << 8) ^ Table[(int)((crc >> 24) ^ value)];
        }

        return crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var crc = i << 24;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
            }

            table[i] = crc;
        }

        return table;
    }
}
