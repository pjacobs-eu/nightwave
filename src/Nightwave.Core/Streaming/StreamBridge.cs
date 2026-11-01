namespace Nightwave.Core.Streaming;

/// <summary>What the bridge did to one stream so far.</summary>
public sealed record BridgeStats
{
    /// <summary>Whether the picture is frozen right now.</summary>
    public bool Active { get; init; }

    /// <summary>How many outages were bridged.</summary>
    public long Outages { get; init; }

    /// <summary>Pictures put in.</summary>
    public long PicturesInserted { get; init; }

    /// <summary>Audio frames of silence put in.</summary>
    public long SilentFramesInserted { get; init; }

    /// <summary>The time spent frozen, in milliseconds.</summary>
    public long FrozenMilliseconds { get; init; }
}

/// <summary>
/// Keeps a stream alive while its picture is missing: no signal for a moment, no key for a
/// moment, or the repair waiting for the next keyframe. Players that run dry stop and buffer, so
/// the last keyframe is sent again as it was received, with new timestamps, and the clock and the
/// audio carry on, until real pictures come back.
/// </summary>
/// <remarks>
/// Nothing is decoded or encoded. The keyframe stays the coded picture the broadcaster sent;
/// only the headers around it change. The rate is low because the picture does not change, and a
/// repeated keyframe costs as much as it did the first time.
/// <para>
/// Fed by one reader. <see cref="Stats"/> may be asked from anywhere.
/// </para>
/// </remarks>
public sealed class StreamBridge
{
    public const int PacketSize = 188;

    /// <summary>How long a picture may be missing before the bridge steps in. Normal gaps between pictures are far shorter.</summary>
    public const long PatienceMs = 500;

    /// <summary>How long a picture is repeated before the outage is left to show as what it is.</summary>
    public const long MaximumFreezeMs = 90_000;

    /// <summary>The distance between repeated pictures.</summary>
    public const long PictureIntervalMs = 200;

    private const int NullPid = 0x1FFF;
    private const int MaxKeyframePackets = 20_000;
    private const long TablesEveryMs = 400;
    private const long Ticks90PerMs = 90;
    private const long Pcr27PerMs = 27_000;
    private const long PcrWrap = (1L << 33) * 300;
    private const long TimestampWrap = 1L << 33;

    /// <summary>The two fields of an interlaced picture arrive no further apart than this.</summary>
    private const long FieldDistance = 25 * Ticks90PerMs;

    private readonly Dictionary<int, PidState> _pids = [];
    private readonly List<byte[]> _capture = [];
    private readonly Func<int, string?> _codec;
    private List<byte[]> _keyframe = [];
    private long _keyframeDts = -1;
    private long _keyframePts = -1;
    private long _capturePts = -1;
    private long _captureDts = -1;
    private int _capturedUnits;
    private bool _capturing;
    private int _videoPid = -1;
    private int _pcrPid = -1;
    private long _lastPcr = -1;
    private long _lastPcrAt;
    private long _decodeDelay = 500 * Ticks90PerMs;
    private long _lastVideoAt = -1;
    private long _lastVideoPts = -1;
    private long _nextPictureAt;
    private long _freezeStartedAt = -1;
    private long _tablesAt;
    private byte[]? _pat;
    private byte[]? _pmt;
    private int _pmtPid = -1;
    private long _outages;
    private long _pictures;
    private long _silentFrames;
    private long _frozenMs;

    /// <param name="codecOf">The codec of a pid, from the program map; null where it is not known.</param>
    public StreamBridge(Func<int, string?> codecOf)
    {
        ArgumentNullException.ThrowIfNull(codecOf);

        _codec = codecOf;
    }

    public BridgeStats Stats => new()
    {
        Active = Volatile.Read(ref _freezeStartedAt) >= 0,
        Outages = Volatile.Read(ref _outages),
        PicturesInserted = Volatile.Read(ref _pictures),
        SilentFramesInserted = Volatile.Read(ref _silentFrames),
        FrozenMilliseconds = Volatile.Read(ref _frozenMs),
    };

    /// <summary>The pid of the program map, for the bridge to keep a copy. Set when it is known.</summary>
    public int ProgramMapPid
    {
        set => _pmtPid = value;
    }

    /// <summary>Whether there is a picture to send and it is time for one.</summary>
    public bool ShouldFill(long nowMs) =>
        _keyframe.Count > 0
        && _lastVideoAt >= 0
        && nowMs - _lastVideoAt >= PatienceMs
        && (_freezeStartedAt < 0 || nowMs - _freezeStartedAt < MaximumFreezeMs)
        && nowMs >= _nextPictureAt;

    /// <summary>Takes whole packets on their way to the client, and makes their counters follow what was put in between.</summary>
    public void Pass(Span<byte> packets, long nowMs)
    {
        for (var offset = 0; offset + PacketSize <= packets.Length; offset += PacketSize)
        {
            Observe(packets.Slice(offset, PacketSize), nowMs);
        }
    }

    /// <summary>Writes one step of the bridge into <paramref name="output"/>.</summary>
    public void Fill(List<byte> output, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(output);

        if (_freezeStartedAt < 0)
        {
            Volatile.Write(ref _freezeStartedAt, nowMs);
            Interlocked.Increment(ref _outages);
        }
        else
        {
            Interlocked.Add(ref _frozenMs, PictureIntervalMs);
        }

        _nextPictureAt = nowMs + PictureIntervalMs;

        var pcr = _lastPcr >= 0 ? (_lastPcr + ((nowMs - _lastPcrAt) * Pcr27PerMs)) % PcrWrap : -1;

        if (nowMs - _tablesAt >= TablesEveryMs)
        {
            _tablesAt = nowMs;
            AppendCopy(output, _pat);
            AppendCopy(output, _pmt);
        }

        if (pcr >= 0 && _pcrPid >= 0)
        {
            output.AddRange(PcrPacket(_pcrPid, pcr, State(_pcrPid)));
        }

        var dts = pcr >= 0 ? ((pcr / 300) + _decodeDelay) % TimestampWrap : (_keyframeDts + ((nowMs - _lastVideoAt) * Ticks90PerMs)) % TimestampWrap;

        AppendPicture(output, dts, pcr);
        Interlocked.Increment(ref _pictures);
        AppendSilence(output, nowMs, dts);
    }

    private static void AppendCopy(List<byte> output, byte[]? packet)
    {
        if (packet is not null)
        {
            output.AddRange(packet);
        }
    }

    private static byte[] PcrPacket(int pid, long pcr, PidState state)
    {
        var packet = new byte[PacketSize];

        packet.AsSpan().Fill(0xFF);
        packet[0] = 0x47;
        packet[1] = (byte)((pid >> 8) & 0x1F);
        packet[2] = (byte)pid;
        packet[3] = (byte)(0x20 | ((state.LastCounter < 0 ? 0 : state.LastCounter) & 0x0F));
        packet[4] = 183;
        packet[5] = 0x10;
        WritePcr(packet.AsSpan(6), pcr);

        return packet;
    }

    private static void WritePcr(Span<byte> field, long pcr)
    {
        var @base = pcr / 300;
        var extension = pcr % 300;

        field[0] = (byte)(@base >> 25);
        field[1] = (byte)(@base >> 17);
        field[2] = (byte)(@base >> 9);
        field[3] = (byte)(@base >> 1);
        field[4] = (byte)((((ulong)@base & 1) << 7) | 0x7E | ((ulong)extension >> 8));
        field[5] = (byte)extension;
    }

    private static long ReadTimestamp(ReadOnlySpan<byte> field) =>
        ((long)((field[0] >> 1) & 7) << 30) | ((long)field[1] << 22) | ((long)(field[2] >> 1) << 15) | ((long)field[3] << 7) | ((long)field[4] >> 1);

    private static void WriteTimestamp(Span<byte> field, long value)
    {
        field[0] = (byte)((uint)(field[0] & 0xF0) | (uint)((value >> 29) & 0x0E) | 1);
        field[1] = (byte)(value >> 22);
        field[2] = (byte)(((value >> 14) & 0xFE) | 1);
        field[3] = (byte)(value >> 7);
        field[4] = (byte)(((value << 1) & 0xFE) | 1);
    }

    private static int PayloadOffset(ReadOnlySpan<byte> packet) =>
        (packet[3] & 0x20) != 0 ? 5 + packet[4] : 4;

    private static bool IsPes(ReadOnlySpan<byte> payload) =>
        payload.Length >= 14 && payload[0] == 0 && payload[1] == 0 && payload[2] == 1;

    private PidState State(int pid)
    {
        if (!_pids.TryGetValue(pid, out var state))
        {
            _pids[pid] = state = new PidState();
        }

        return state;
    }

    private void Observe(Span<byte> packet, long nowMs)
    {
        var pid = ((packet[1] & 0x1F) << 8) | packet[2];

        if (pid == NullPid)
        {
            return;
        }

        var state = State(pid);
        var hasPayload = (packet[3] & 0x10) != 0;

        if (hasPayload)
        {
            if (state.Bridged)
            {
                // The bridge used this pid. What follows carries on from there.
                state.Delta = ((state.LastCounter + 1) - (packet[3] & 0x0F)) & 0x0F;
                state.Bridged = false;
            }

            var counter = ((packet[3] & 0x0F) + state.Delta) & 0x0F;

            packet[3] = (byte)((packet[3] & 0xF0) | counter);
            state.LastCounter = counter;
        }

        var hasAdaptation = (packet[3] & 0x20) != 0;

        if (hasAdaptation && packet[4] >= 7 && (packet[5] & 0x10) != 0 && (_pcrPid < 0 || pid == _pcrPid))
        {
            var field = packet.Slice(6, 6);
            var @base = ((long)field[0] << 25) | ((long)field[1] << 17) | ((long)field[2] << 9) | ((long)field[3] << 1) | ((long)field[4] >> 7);

            _pcrPid = pid;
            _lastPcr = (@base * 300) + (((field[4] & 1) << 8) | field[5]);
            _lastPcrAt = nowMs;
        }

        if (pid == 0 && (packet[1] & 0x40) != 0)
        {
            _pat = packet.ToArray();
        }
        else if (pid == _pmtPid && (packet[1] & 0x40) != 0)
        {
            _pmt = packet.ToArray();
        }

        // Any packet of the picture says it is alive: a large picture spans a long way, and not
        // every start of one carries a timestamp.
        if (pid == _videoPid && hasPayload)
        {
            _lastVideoAt = nowMs;

            if (_freezeStartedAt >= 0)
            {
                Volatile.Write(ref _freezeStartedAt, -1);
            }
        }

        var offset = PayloadOffset(packet);

        if (!hasPayload || offset >= PacketSize)
        {
            return;
        }

        var payload = packet[offset..];
        var unitStart = (packet[1] & 0x40) != 0;

        if (unitStart && IsPes(payload))
        {
            var stream = payload[3];

            if (stream is >= 0xE0 and <= 0xEF)
            {
                Picture(packet, payload, pid, nowMs);
            }
            else if ((stream is >= 0xC0 and <= 0xDF) || stream == 0xBD)
            {
                Audio(payload, state, nowMs);
            }
        }

        if (_capturing && pid == _videoPid)
        {
            Capture(packet);
        }
    }

    private void Picture(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> pes, int pid, long nowMs)
    {
        var randomAccess = (packet[3] & 0x20) != 0 && packet[4] > 0 && (packet[5] & 0x40) != 0;

        if (_videoPid < 0 && !randomAccess)
        {
            return;
        }

        var flags = pes[7] >> 6;

        if (flags < 2)
        {
            return;
        }

        var pts = ReadTimestamp(pes[9..]);
        var dts = flags == 3 ? ReadTimestamp(pes[14..]) : pts;

        // Real pictures are back: the outage is over.
        if (pid == _videoPid || _videoPid < 0)
        {
            _videoPid = pid;

            if (_freezeStartedAt >= 0)
            {
                Volatile.Write(ref _freezeStartedAt, -1);
            }

            _lastVideoAt = nowMs;
            _lastVideoPts = pts;
        }

        if (_capturing)
        {
            if (_capturedUnits == 1 && Math.Abs(pts - _capturePts) <= FieldDistance)
            {
                _capturedUnits = 2;
            }
            else
            {
                FinishCapture();
            }
        }

        if (randomAccess && !_capturing)
        {
            _capture.Clear();
            _capturing = true;
            _capturedUnits = 1;
            _capturePts = pts;
            _captureDts = dts;

            if (_lastPcr >= 0)
            {
                var delay = (dts - (_lastPcr / 300) + TimestampWrap) % TimestampWrap;

                if (delay is > 20 * Ticks90PerMs and < 2000 * Ticks90PerMs)
                {
                    _decodeDelay = delay;
                }
            }
        }
    }

    private void Capture(ReadOnlySpan<byte> packet)
    {
        _capture.Add(packet.ToArray());

        if (_capture.Count > MaxKeyframePackets)
        {
            _capture.Clear();
            _capturing = false;
        }
    }

    private void FinishCapture()
    {
        _capturing = false;

        if (_capture.Count > 0)
        {
            _keyframe = [.. _capture];
            _keyframePts = _capturePts;
            _keyframeDts = _captureDts;
            _capture.Clear();
        }
    }

    private void AppendPicture(List<byte> output, long dts, long pcr)
    {
        var state = State(_videoPid);
        var first = _keyframePts;
        var offsetPts = (first - _keyframeDts + TimestampWrap) % TimestampWrap;

        foreach (var original in _keyframe)
        {
            var packet = (byte[])original.Clone();
            var counter = (state.LastCounter + 1) & 0x0F;

            packet[3] = (byte)((packet[3] & 0xF0) | counter);
            state.LastCounter = counter;
            state.Bridged = true;

            if ((packet[3] & 0x20) != 0 && packet[4] >= 7 && (packet[5] & 0x10) != 0 && pcr >= 0)
            {
                WritePcr(packet.AsSpan(6), pcr);
            }

            var offset = PayloadOffset(packet);

            if ((packet[1] & 0x40) != 0 && offset < PacketSize && IsPes(packet.AsSpan(offset)))
            {
                var pes = packet.AsSpan(offset);
                var flags = pes[7] >> 6;

                if (flags >= 2)
                {
                    // A second field keeps its distance from the first.
                    var step = (ReadTimestamp(pes[9..]) - first + TimestampWrap) % TimestampWrap;

                    if (step > FieldDistance)
                    {
                        step = 0;
                    }

                    WriteTimestamp(pes[9..], (dts + offsetPts + step) % TimestampWrap);

                    if (flags == 3)
                    {
                        WriteTimestamp(pes[14..], (dts + step) % TimestampWrap);
                    }
                }
            }

            output.AddRange(packet);
        }
    }

    private static void Audio(ReadOnlySpan<byte> pes, PidState state, long nowMs)
    {
        var flags = pes[7] >> 6;

        if (flags < 2)
        {
            return;
        }

        state.LastAudioPts = ReadTimestamp(pes[9..]);
        state.LastAudioAt = nowMs;
        state.PesHeader = pes[..(9 + pes[8])].ToArray();

        if (state.Silence is null)
        {
            var payload = pes[(9 + pes[8])..];

            state.AudioSeen = true;
            state.Header = payload[..Math.Min(8, payload.Length)].ToArray();
        }
    }

    private void AppendSilence(List<byte> output, long nowMs, long dts)
    {
        foreach (var (pid, state) in _pids)
        {
            if (!state.AudioSeen || nowMs - state.LastAudioAt < PatienceMs)
            {
                continue;
            }

            if (state.Silence is null && !state.SilenceTried)
            {
                state.SilenceTried = true;

                switch (_codec(pid))
                {
                    case "MPEG Audio":
                        state.Silence = SilentAudio.MpegFrame(state.Header);
                        state.FrameTicks = SilentAudio.MpegSamples * 90000L / 48000;
                        break;
                    case "AC-3":
                        state.Silence = SilentAudio.Ac3Frame(state.Header);
                        state.FrameTicks = SilentAudio.Ac3Samples * 90000L / 48000;
                        break;
                }
            }

            if (state.Silence is null)
            {
                continue;
            }

            // From where the audio stopped, in step with what is decoded next, in one PES.
            var from = state.NextSilencePts >= 0 ? state.NextSilencePts : (state.LastAudioPts + state.FrameTicks) % TimestampWrap;
            var until = (dts + (PictureIntervalMs * 2 * Ticks90PerMs)) % TimestampWrap;
            var frames = Math.Clamp((until - from + TimestampWrap) % TimestampWrap / state.FrameTicks, 1, 20);

            AppendAudio(output, pid, state, from, (int)frames);
            state.NextSilencePts = (from + (frames * state.FrameTicks)) % TimestampWrap;
            Interlocked.Add(ref _silentFrames, frames);
        }

        foreach (var state in _pids.Values)
        {
            if (nowMs - state.LastAudioAt < PatienceMs)
            {
                state.NextSilencePts = -1;
            }
        }
    }

    private static void AppendAudio(List<byte> output, int pid, PidState state, long pts, int frames)
    {
        var silence = state.Silence!;
        var header = new byte[14];

        // A plain PES header: the original may carry options that no longer hold.
        header[2] = 1;
        header[3] = state.PesHeader![3];

        var length = 8 + (silence.Length * frames);

        header[4] = (byte)(length >> 8);
        header[5] = (byte)length;
        header[6] = 0x80;
        header[7] = 0x80;
        header[8] = 5;
        header[9] = 0x21;
        WriteTimestamp(header.AsSpan(9), pts);

        var data = new byte[header.Length + (silence.Length * frames)];

        header.CopyTo(data, 0);

        for (var frame = 0; frame < frames; frame++)
        {
            silence.CopyTo(data, header.Length + (frame * silence.Length));
        }

        for (var offset = 0; offset < data.Length; offset += PacketSize - 4)
        {
            var packet = new byte[PacketSize];
            var left = data.Length - offset;
            var counter = (state.LastCounter + 1) & 0x0F;

            state.LastCounter = counter;
            state.Bridged = true;

            packet[0] = 0x47;
            packet[1] = (byte)((offset == 0 ? 0x40 : 0) | ((pid >> 8) & 0x1F));
            packet[2] = (byte)pid;

            if (left >= PacketSize - 4)
            {
                packet[3] = (byte)(0x10 | counter);
                data.AsSpan(offset, PacketSize - 4).CopyTo(packet.AsSpan(4));
            }
            else
            {
                // The last one is stuffed through its adaptation field.
                var stuffing = PacketSize - 4 - left;

                packet[3] = (byte)(0x30 | counter);
                packet[4] = (byte)(stuffing - 1);

                if (stuffing > 1)
                {
                    packet[5] = 0;
                    packet.AsSpan(6, stuffing - 2).Fill(0xFF);
                }

                data.AsSpan(offset, left).CopyTo(packet.AsSpan(4 + stuffing));
            }

            output.AddRange(packet);
        }
    }

    private sealed class PidState
    {
        public int LastCounter { get; set; } = -1;

        public int Delta { get; set; }

        public bool Bridged { get; set; }

        public bool AudioSeen { get; set; }

        public long LastAudioPts { get; set; }

        public long LastAudioAt { get; set; }

        public long NextSilencePts { get; set; } = -1;

        public long FrameTicks { get; set; } = 1920;

        public byte[]? PesHeader { get; set; }

        public byte[] Header { get; set; } = [];

        public byte[]? Silence { get; set; }

        public bool SilenceTried { get; set; }
    }
}
