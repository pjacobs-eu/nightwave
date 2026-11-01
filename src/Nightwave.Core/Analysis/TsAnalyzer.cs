namespace Nightwave.Core.Analysis;

/// <summary>One packet identifier of a stream, as it stands.</summary>
public sealed record PidSummary
{
    public required int Pid { get; init; }

    /// <summary><c>video</c>, <c>audio</c>, <c>subtitle</c>, <c>teletext</c>, <c>table</c>, <c>data</c> or <c>other</c>.</summary>
    public required string Kind { get; init; }

    public string? Codec { get; init; }

    public string? Language { get; init; }

    public int? StreamType { get; init; }

    public long Packets { get; init; }

    public long ContinuityErrors { get; init; }

    public bool Scrambled { get; init; }

    public bool CarriesPcr { get; init; }

    public AudioFormat? Audio { get; init; }

    /// <summary>Bits per second over the last sample, filled in by whoever samples.</summary>
    public double BitRate { get; init; }
}

/// <summary>One conditional access system a program map names, and the pid of its ECMs.</summary>
public sealed record CaEntry(int System, int EcmPid);

/// <summary>A stream as measured so far. Counters run from the start of the session.</summary>
public sealed record StreamAnalysis
{
    public long Packets { get; init; }

    public long Bytes { get; init; }

    public int? ProgramNumber { get; init; }

    public int? PmtPid { get; init; }

    public int? PcrPid { get; init; }

    public VideoFormat? Video { get; init; }

    public IReadOnlyList<PidSummary> Pids { get; init; } = [];

    /// <summary>Conditional access systems the program map names.</summary>
    public IReadOnlyList<int> CaSystems { get; init; } = [];

    /// <summary>The same with the pid of each system's ECMs.</summary>
    public IReadOnlyList<CaEntry> CaEntries { get; init; } = [];

    /// <summary>TR 101 290 1.1: bytes given up on to find the packet boundary again.</summary>
    public long SyncLossBytes { get; init; }

    /// <summary>TR 101 290 1.4.</summary>
    public long ContinuityErrors { get; init; }

    /// <summary>TR 101 290 2.1: packets the tuner flagged as undecodable.</summary>
    public long TransportErrors { get; init; }

    /// <summary>TR 101 290 2.3a: PCR gaps over 100 ms.</summary>
    public long PcrIntervalErrors { get; init; }

    /// <summary>TR 101 290 1.3a: gaps between two PATs over 500 ms.</summary>
    public long PatIntervalErrors { get; init; }

    public double? PcrIntervalAverageMs { get; init; }

    public double? PcrIntervalMaximumMs { get; init; }
}

/// <summary>
/// Measures a transport stream as it passes: everything here comes from packet headers, tables
/// and the headers of the elementary streams. Nothing is decoded and no byte is changed, so it
/// works for a satellite service and a web stream alike.
/// </summary>
/// <remarks>Thread safe: fed by one reader, asked from anywhere.</remarks>
public sealed class TsAnalyzer
{
    private const int PacketSize = 188;
    private const int NullPid = 0x1FFF;
    private const int SectionLimit = 4096;

    /// <summary>How much of a video PES is kept: the sequence header sits at its very start.</summary>
    private const int VideoProbe = 8192;

    private const long PcrHertz = 27_000_000;
    private const long PcrWrap = (1L << 33) * 300;

    private readonly Lock _gate = new();
    private readonly byte[] _carry = new byte[PacketSize];
    private readonly Dictionary<int, Pid> _pids = [];
    private readonly List<CaEntry> _caEntries = [];
    private readonly TimeProvider _time;
    private int _carried;
    private long _packets;
    private long _bytes;
    private long _syncLoss;
    private long _continuity;
    private long _transport;
    private long _pcrErrors;
    private long _patErrors;
    private long _pcrSum;
    private long _pcrCount;
    private long _pcrMaximum;
    private long _lastPcr = -1;
    private long _lastPat = -1;
    private int? _programNumber;
    private int? _pmtPid;
    private int? _pcrPid;
    private VideoFormat? _video;
    private int _videoPid = -1;
    private long _lastPts = -1;
    private long _smallestPtsStep = long.MaxValue;

    public TsAnalyzer(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public StreamAnalysis Snapshot()
    {
        lock (_gate)
        {
            var video = _video;

            if (video is { FrameRate: null } && _smallestPtsStep is > 0 and < 90000)
            {
                video = video with { FrameRate = 90000d / _smallestPtsStep };
            }

            return new StreamAnalysis
            {
                Packets = _packets,
                Bytes = _bytes,
                ProgramNumber = _programNumber,
                PmtPid = _pmtPid,
                PcrPid = _pcrPid,
                Video = video,
                Pids = [.. _pids.OrderBy(entry => entry.Key).Select(entry => entry.Value.Summary(entry.Key, entry.Key == _pcrPid))],
                CaSystems = [.. _caEntries.Select(entry => entry.System).Distinct()],
                CaEntries = [.. _caEntries],
                SyncLossBytes = _syncLoss,
                ContinuityErrors = _continuity,
                TransportErrors = _transport,
                PcrIntervalErrors = _pcrErrors,
                PatIntervalErrors = _patErrors,
                PcrIntervalAverageMs = _pcrCount > 0 ? _pcrSum * 1000d / PcrHertz / _pcrCount : null,
                PcrIntervalMaximumMs = _pcrCount > 0 ? _pcrMaximum * 1000d / PcrHertz : null,
            };
        }
    }

    /// <summary>Takes the next part of the stream, whole packets or not.</summary>
    public void Inspect(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            _bytes += data.Length;

            if (_carried > 0)
            {
                var take = Math.Min(PacketSize - _carried, data.Length);

                data[..take].CopyTo(_carry.AsSpan(_carried));
                _carried += take;
                data = data[take..];

                if (_carried < PacketSize)
                {
                    return;
                }

                _carried = 0;

                if (_carry[0] == 0x47)
                {
                    Packet(_carry);
                }
                else
                {
                    _syncLoss += PacketSize;
                }
            }

            while (!data.IsEmpty)
            {
                if (data[0] != 0x47)
                {
                    var next = data.IndexOf((byte)0x47);
                    var skip = next < 0 ? data.Length : next;

                    _syncLoss += skip;
                    data = data[skip..];

                    continue;
                }

                if (data.Length < PacketSize)
                {
                    data.CopyTo(_carry);
                    _carried = data.Length;

                    return;
                }

                Packet(data[..PacketSize]);
                data = data[PacketSize..];
            }
        }
    }

    private void Packet(ReadOnlySpan<byte> packet)
    {
        _packets++;

        if ((packet[1] & 0x80) != 0)
        {
            _transport++;

            return;
        }

        var number = ((packet[1] & 0x1F) << 8) | packet[2];
        var unitStart = (packet[1] & 0x40) != 0;
        var scrambling = packet[3] >> 6;
        var hasAdaptation = (packet[3] & 0x20) != 0;
        var hasPayload = (packet[3] & 0x10) != 0;
        var counter = packet[3] & 0x0F;

        if (!_pids.TryGetValue(number, out var pid))
        {
            _pids[number] = pid = new Pid();

            if (number == 0)
            {
                pid.Kind = "table";
                pid.Codec = "PAT";
            }
            else if (number == NullPid)
            {
                pid.Kind = "other";
                pid.Codec = "Null";
            }
        }

        pid.Packets++;
        pid.Scrambled = scrambling != 0;

        var offset = 4;

        if (hasAdaptation)
        {
            var length = packet[4];

            offset = 5 + length;

            if (length > 0 && number == _pcrPid && (packet[5] & 0x10) != 0 && length >= 7)
            {
                Pcr(packet[6..12], (packet[5] & 0x80) != 0);
            }
        }

        if (number != NullPid && hasPayload)
        {
            var discontinuity = hasAdaptation && packet[4] > 0 && (packet[5] & 0x80) != 0;

            if (pid.LastCounter >= 0 && !discontinuity && counter != pid.LastCounter && counter != ((pid.LastCounter + 1) & 0x0F))
            {
                pid.ContinuityErrors++;
                _continuity++;
            }

            pid.LastCounter = counter;
        }

        if (!hasPayload || offset >= PacketSize || scrambling != 0)
        {
            return;
        }

        var payload = packet[offset..];

        if (number == 0 || number == _pmtPid)
        {
            Section(pid, number, payload, unitStart);
        }
        else if (number == _videoPid)
        {
            Video(pid, payload, unitStart);
        }
        else if (unitStart && pid.Audio is null && pid.Kind == "audio" && pid.Codec is { } codec)
        {
            pid.Audio = AudioHeaders.Find(PesPayload(payload), codec);
        }
    }

    private void Pcr(ReadOnlySpan<byte> field, bool discontinuity)
    {
        var @base = ((long)field[0] << 25) | ((long)field[1] << 17) | ((long)field[2] << 9) | ((long)field[3] << 1) | ((long)field[4] >> 7);
        var extension = ((field[4] & 1) << 8) | field[5];
        var pcr = (@base * 300) + extension;

        if (_lastPcr >= 0 && !discontinuity)
        {
            var step = (pcr - _lastPcr + PcrWrap) % PcrWrap;

            // A jump of minutes is a stream that restarted, not an interval.
            if (step < PcrHertz)
            {
                _pcrSum += step;
                _pcrCount++;
                _pcrMaximum = Math.Max(_pcrMaximum, step);

                if (step > PcrHertz / 10)
                {
                    _pcrErrors++;
                }
            }
        }

        _lastPcr = pcr;
    }

    private void Video(Pid pid, ReadOnlySpan<byte> payload, bool unitStart)
    {
        if (unitStart)
        {
            Pts(payload);

            if (_video is null && pid.Probe.Count > 0 && pid.Codec is { } codec)
            {
                _video = VideoHeaders.Find(pid.Probe.ToArray(), codec);
            }

            pid.Probe.Clear();
            payload = PesPayload(payload);
        }

        if (_video is null && pid.Probe.Count < VideoProbe)
        {
            pid.Probe.AddRange(payload);
        }
    }

    private void Pts(ReadOnlySpan<byte> pes)
    {
        if (pes.Length < 14 || pes[0] != 0 || pes[1] != 0 || pes[2] != 1 || (pes[7] & 0x80) == 0)
        {
            return;
        }

        var pts = ((long)((pes[9] >> 1) & 7) << 30) | ((long)pes[10] << 22) | ((long)(pes[11] >> 1) << 15) | ((long)pes[12] << 7) | ((long)pes[13] >> 1);

        if (_lastPts >= 0)
        {
            var step = Math.Abs(pts - _lastPts);

            if (step > 0)
            {
                _smallestPtsStep = Math.Min(_smallestPtsStep, step);
            }
        }

        _lastPts = pts;
    }

    private static ReadOnlySpan<byte> PesPayload(ReadOnlySpan<byte> pes) =>
        pes.Length > 9 && pes[0] == 0 && pes[1] == 0 && pes[2] == 1 && 9 + pes[8] < pes.Length ? pes[(9 + pes[8])..] : pes;

    private void Section(Pid pid, int number, ReadOnlySpan<byte> payload, bool unitStart)
    {
        if (unitStart)
        {
            var pointer = payload[0];

            if (1 + pointer >= payload.Length)
            {
                return;
            }

            pid.Probe.Clear();
            payload = payload[(1 + pointer)..];
        }
        else if (pid.Probe.Count == 0)
        {
            return;
        }

        pid.Probe.AddRange(payload);

        if (pid.Probe.Count < 3)
        {
            return;
        }

        var length = 3 + (((pid.Probe[1] & 0x0F) << 8) | pid.Probe[2]);

        if (length > SectionLimit)
        {
            pid.Probe.Clear();
        }
        else if (pid.Probe.Count >= length)
        {
            var section = pid.Probe.GetRange(0, length).ToArray();

            pid.Probe.Clear();

            if (number == 0)
            {
                Pat(section);
            }
            else
            {
                Pmt(section);
            }
        }
    }

    private void Pat(ReadOnlySpan<byte> section)
    {
        var now = _time.GetTimestamp();

        if (_lastPat >= 0 && _time.GetElapsedTime(_lastPat, now) > TimeSpan.FromMilliseconds(500))
        {
            _patErrors++;
        }

        _lastPat = now;

        for (var offset = 8; offset + 8 <= section.Length; offset += 4)
        {
            var program = (section[offset] << 8) | section[offset + 1];

            if (program != 0)
            {
                _programNumber = program;
                _pmtPid = ((section[offset + 2] & 0x1F) << 8) | section[offset + 3];

                if (!_pids.TryGetValue(_pmtPid.Value, out var pmt))
                {
                    _pids[_pmtPid.Value] = pmt = new Pid();
                }

                pmt.Kind = "table";
                pmt.Codec = "PMT";

                return;
            }
        }
    }

    private void Pmt(ReadOnlySpan<byte> section)
    {
        if (section.Length < 16)
        {
            return;
        }

        _pcrPid = ((section[8] & 0x1F) << 8) | section[9];

        var infoLength = ((section[10] & 0x0F) << 8) | section[11];
        var end = section.Length - 4;

        _caEntries.Clear();
        ReadCaSystems(section.Slice(12, Math.Min(infoLength, Math.Max(0, end - 12))));

        for (var offset = 12 + infoLength; offset + 5 <= end;)
        {
            var type = section[offset];
            var number = ((section[offset + 1] & 0x1F) << 8) | section[offset + 2];
            var length = ((section[offset + 3] & 0x0F) << 8) | section[offset + 4];
            var descriptors = section.Slice(offset + 5, Math.Min(length, end - offset - 5));

            if (!_pids.TryGetValue(number, out var pid))
            {
                _pids[number] = pid = new Pid();
            }

            Classify(pid, type, descriptors);
            ReadCaSystems(descriptors);

            if (pid.Kind == "video" && _videoPid < 0)
            {
                _videoPid = number;
            }

            offset += 5 + length;
        }
    }

    private void ReadCaSystems(ReadOnlySpan<byte> descriptors)
    {
        for (var offset = 0; offset + 2 <= descriptors.Length;)
        {
            var length = descriptors[offset + 1];

            if (descriptors[offset] == 0x09 && length >= 4 && offset + 6 <= descriptors.Length)
            {
                var entry = new CaEntry(
                    (descriptors[offset + 2] << 8) | descriptors[offset + 3],
                    ((descriptors[offset + 4] & 0x1F) << 8) | descriptors[offset + 5]);

                if (!_caEntries.Contains(entry))
                {
                    _caEntries.Add(entry);
                }
            }

            offset += 2 + length;
        }
    }

    private static void Classify(Pid pid, int type, ReadOnlySpan<byte> descriptors)
    {
        pid.StreamType = type;

        (pid.Kind, pid.Codec) = type switch
        {
            0x01 or 0x02 => ("video", "MPEG-2"),
            0x1B => ("video", "H.264"),
            0x24 => ("video", "HEVC"),
            0x33 => ("video", "VVC"),
            0x03 or 0x04 => ("audio", "MPEG Audio"),
            0x0F => ("audio", "AAC"),
            0x11 => ("audio", "HE-AAC"),
            0x81 => ("audio", "AC-3"),
            0x87 => ("audio", "E-AC-3"),
            0x05 => ("data", "Private Sections"),
            0x06 => ("data", "Private PES"),
            0x0B => ("data", "DSM-CC"),
            _ => ("data", (string?)null),
        };

        for (var offset = 0; offset + 2 <= descriptors.Length;)
        {
            var tag = descriptors[offset];
            var length = descriptors[offset + 1];
            var body = descriptors.Slice(Math.Min(offset + 2, descriptors.Length), Math.Min(length, Math.Max(0, descriptors.Length - offset - 2)));

            switch (tag)
            {
                case 0x0A when body.Length >= 3:
                    pid.Language = Language(body);
                    break;
                case 0x6A:
                    (pid.Kind, pid.Codec) = ("audio", "AC-3");
                    break;
                case 0x7A:
                    (pid.Kind, pid.Codec) = ("audio", "E-AC-3");
                    break;
                case 0x7C:
                    (pid.Kind, pid.Codec) = ("audio", "AAC");
                    break;
                case 0x56:
                    (pid.Kind, pid.Codec) = ("teletext", "Teletext");

                    if (body.Length >= 3)
                    {
                        pid.Language = Language(body);
                    }

                    break;
                case 0x59:
                    (pid.Kind, pid.Codec) = ("subtitle", "DVB Subtitles");

                    if (body.Length >= 3)
                    {
                        pid.Language = Language(body);
                    }

                    break;
            }

            offset += 2 + length;
        }
    }

    private static string? Language(ReadOnlySpan<byte> code)
    {
        var text = System.Text.Encoding.ASCII.GetString(code[..3]);

        return text.All(char.IsLetter) ? text.ToLowerInvariant() : null;
    }

    private sealed class Pid
    {
        public string Kind { get; set; } = "other";

        public string? Codec { get; set; }

        public string? Language { get; set; }

        public int? StreamType { get; set; }

        public long Packets { get; set; }

        public long ContinuityErrors { get; set; }

        public bool Scrambled { get; set; }

        public int LastCounter { get; set; } = -1;

        public AudioFormat? Audio { get; set; }

        public List<byte> Probe { get; } = [];

        public PidSummary Summary(int number, bool carriesPcr) => new()
        {
            Pid = number,
            Kind = Kind,
            Codec = Codec,
            Language = Language,
            StreamType = StreamType,
            Packets = Packets,
            ContinuityErrors = ContinuityErrors,
            Scrambled = Scrambled,
            CarriesPcr = carriesPcr,
            Audio = Audio,
        };
    }
}
