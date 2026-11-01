namespace Nightwave.Core.Streaming;

/// <summary>What the repair did to one stream so far.</summary>
public sealed record TsRepairStats
{
    public long PacketsIn { get; init; }

    public long PacketsOut { get; init; }

    /// <summary>Bytes skipped to find the packet boundary again.</summary>
    public long BytesResynchronised { get; init; }

    /// <summary>Packets the tuner flagged as undecodable, dropped.</summary>
    public long ErrorPacketsDropped { get; init; }

    /// <summary>Packets sent twice by the source, dropped.</summary>
    public long DuplicatesDropped { get; init; }

    /// <summary>Times a continuity counter showed that packets were missing.</summary>
    public long GapsFound { get; init; }

    /// <summary>Packets held back after a gap until the stream could be resumed cleanly.</summary>
    public long PacketsHeldBack { get; init; }

    /// <summary>Packets of pictures that refer to what came before a keyframe, left out behind it.</summary>
    public long LeadingPacketsDropped { get; init; }
}

/// <summary>
/// Repairs a transport stream at packet level, the way a receiver's demultiplexer does. No
/// elementary stream is decoded or re-encoded: what is lost stays lost, and the repair decides
/// what a player gets to see around the hole.
/// </summary>
/// <remarks>
/// A packet the tuner's forward error correction gave up on is dropped rather than handed on as
/// garbage. A gap in a pid's continuity counter means the unit in progress is damaged, so the pid
/// is held back until something can be started on again: the next random access point on a pid
/// that carries them, so a decoder shows the last good picture instead of macroblocks, otherwise
/// the next payload unit. Pictures that follow a keyframe in the stream but are shown before it
/// refer to what was lost, so they are left out as well: those are the blocks of a resume. Continuity counters of what is passed are written anew, so a decoder
/// that stalls on a discontinuity sees a stream that starts cleanly at a unit boundary.
/// <para>
/// Not thread safe: one instance per reader of a stream.
/// </para>
/// </remarks>
public sealed class TsRepairFilter
{
    public const int PacketSize = 188;

    private const byte SyncByte = 0x47;
    private const int NullPid = 0x1FFF;
    private const int PidCount = 0x2000;
    private const long TimestampWrap = 1L << 33;

    /// <summary>
    /// How many packets a pid is held back for a random access point before it settles for the
    /// next unit start: a broadcaster that stops marking them must not end the picture.
    /// </summary>
    private const int MaxHeldForRandomAccess = 20_000;

    private readonly PidState?[] _pids = new PidState?[PidCount];
    private readonly byte[] _carry = new byte[PacketSize * 2];
    private int _carried;

    private long _packetsIn;
    private long _packetsOut;
    private long _bytesResynchronised;
    private long _errorPacketsDropped;
    private long _duplicatesDropped;
    private long _gapsFound;
    private long _packetsHeldBack;
    private long _leadingPacketsDropped;

    public TsRepairStats Stats => new()
    {
        PacketsIn = Volatile.Read(ref _packetsIn),
        PacketsOut = Volatile.Read(ref _packetsOut),
        BytesResynchronised = Volatile.Read(ref _bytesResynchronised),
        ErrorPacketsDropped = Volatile.Read(ref _errorPacketsDropped),
        DuplicatesDropped = Volatile.Read(ref _duplicatesDropped),
        GapsFound = Volatile.Read(ref _gapsFound),
        PacketsHeldBack = Volatile.Read(ref _packetsHeldBack),
        LeadingPacketsDropped = Volatile.Read(ref _leadingPacketsDropped),
    };

    /// <summary>
    /// Takes the next part of a stream, whole packets or not, and writes what passes to
    /// <paramref name="output"/>, which has to hold at least <paramref name="input"/>'s length
    /// plus one packet. Returns how many bytes were written.
    /// </summary>
    public int Process(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var written = 0;

        // A packet split across two reads is completed from the carry first.
        while (_carried > 0 && !input.IsEmpty)
        {
            var take = Math.Min(PacketSize - _carried, input.Length);

            input[..take].CopyTo(_carry.AsSpan(_carried));
            _carried += take;
            input = input[take..];

            if (_carried < PacketSize)
            {
                return written;
            }

            if (_carry[0] == SyncByte && (input.IsEmpty || input[0] == SyncByte))
            {
                written += Pass(_carry.AsSpan(0, PacketSize), output[written..]);
                _carried = 0;
            }
            else
            {
                // Not a packet after all: find the next sync byte inside what was carried.
                var next = _carry.AsSpan(1, PacketSize - 1).IndexOf(SyncByte);
                var skip = next < 0 ? PacketSize : next + 1;

                Interlocked.Add(ref _bytesResynchronised, skip);
                _carried = PacketSize - skip;
                _carry.AsSpan(skip, _carried).CopyTo(_carry);
            }
        }

        while (!input.IsEmpty)
        {
            if (input[0] != SyncByte || (input.Length > PacketSize && input[PacketSize] != SyncByte))
            {
                var next = input[1..].IndexOf(SyncByte);
                var skip = next < 0 ? input.Length : next + 1;

                Interlocked.Add(ref _bytesResynchronised, skip);
                input = input[skip..];

                continue;
            }

            if (input.Length < PacketSize)
            {
                input.CopyTo(_carry);
                _carried = input.Length;

                break;
            }

            written += Pass(input[..PacketSize], output[written..]);
            input = input[PacketSize..];
        }

        return written;
    }

    private int Pass(ReadOnlySpan<byte> packet, Span<byte> output)
    {
        Interlocked.Increment(ref _packetsIn);

        var pid = ((packet[1] & 0x1F) << 8) | packet[2];

        if ((packet[1] & 0x80) != 0)
        {
            Interlocked.Increment(ref _errorPacketsDropped);

            // The pid in a damaged header cannot be trusted, so no state is touched: the counter
            // of the real pid shows the loss on its next good packet.
            return 0;
        }

        if (pid == NullPid)
        {
            return Copy(packet, output, null);
        }

        var state = _pids[pid] ??= new PidState();
        var hasPayload = (packet[3] & 0x10) != 0;
        var counter = packet[3] & 0x0F;
        var unitStart = (packet[1] & 0x40) != 0;
        var randomAccess = (packet[3] & 0x20) != 0 && packet[4] > 0 && (packet[5] & 0x40) != 0;

        state.CarriesRandomAccess |= randomAccess;

        var discontinuityFlagged = (packet[3] & 0x20) != 0 && packet[4] > 0 && (packet[5] & 0x80) != 0;

        if (hasPayload)
        {
            if (state.LastCounter >= 0 && !discontinuityFlagged)
            {
                if (counter == state.LastCounter)
                {
                    // The standard allows one repeat, and a repeat carries nothing new.
                    Interlocked.Increment(ref _duplicatesDropped);

                    return 0;
                }

                if (counter != ((state.LastCounter + 1) & 0x0F))
                {
                    Interlocked.Increment(ref _gapsFound);
                    state.Holding = true;
                    state.Held = 0;
                }
            }

            state.LastCounter = counter;
        }

        if (state.Holding)
        {
            var mayResume = state.CarriesRandomAccess && state.Held < MaxHeldForRandomAccess
                ? randomAccess
                : unitStart;

            if (!mayResume)
            {
                state.Held++;
                Interlocked.Increment(ref _packetsHeldBack);

                return 0;
            }

            state.Holding = false;
            state.ResumePts = randomAccess ? Pts(packet) : -1;
        }
        else if (randomAccess && state.PicturesSeen == 0)
        {
            // Joining is a resume too: nothing before this keyframe was ever seen.
            state.ResumePts = Pts(packet);
        }

        if (randomAccess)
        {
            state.PicturesSeen++;
        }

        if (state.ResumePts >= 0 && unitStart)
        {
            var pts = Pts(packet);

            if (pts >= 0)
            {
                var ahead = (pts - state.ResumePts + TimestampWrap) % TimestampWrap;

                if (ahead > TimestampWrap / 2)
                {
                    state.SkippingLeading = true;
                }
                else
                {
                    state.SkippingLeading = false;

                    if (ahead > 0)
                    {
                        state.ResumePts = -1;
                    }
                }
            }
        }

        if (state.SkippingLeading)
        {
            Interlocked.Increment(ref _leadingPacketsDropped);

            return 0;
        }

        return Copy(packet, output, hasPayload ? state : null);
    }

    /// <summary>The presentation time of the PES a packet starts, or -1.</summary>
    private static long Pts(ReadOnlySpan<byte> packet)
    {
        var offset = (packet[3] & 0x20) != 0 ? 5 + packet[4] : 4;

        if ((packet[1] & 0x40) == 0 || offset + 14 > PacketSize || packet[offset] != 0 || packet[offset + 1] != 0 || packet[offset + 2] != 1 || (packet[offset + 7] & 0x80) == 0)
        {
            return -1;
        }

        var field = packet[(offset + 9)..];

        return ((long)((field[0] >> 1) & 7) << 30) | ((long)field[1] << 22) | ((long)(field[2] >> 1) << 15) | ((long)field[3] << 7) | ((long)field[4] >> 1);
    }

    private int Copy(ReadOnlySpan<byte> packet, Span<byte> output, PidState? numbered)
    {
        packet.CopyTo(output);

        if (numbered is not null)
        {
            // Packets without payload do not count; everything else follows on from what went
            // out, so nothing left out here looks like a loss downstream.
            numbered.OutputCounter = numbered.OutputCounter < 0 ? output[3] & 0x0F : (numbered.OutputCounter + 1) & 0x0F;
            output[3] = (byte)((output[3] & 0xF0) | numbered.OutputCounter);
        }

        Interlocked.Increment(ref _packetsOut);

        return PacketSize;
    }

    private sealed class PidState
    {
        public int LastCounter { get; set; } = -1;

        public int OutputCounter { get; set; } = -1;

        public bool Holding { get; set; }

        public int Held { get; set; }

        public bool CarriesRandomAccess { get; set; }

        /// <summary>The keyframe resumed on, while pictures from before it may still follow; -1 otherwise.</summary>
        public long ResumePts { get; set; } = -1;

        public bool SkippingLeading { get; set; }

        public long PicturesSeen { get; set; }
    }
}
