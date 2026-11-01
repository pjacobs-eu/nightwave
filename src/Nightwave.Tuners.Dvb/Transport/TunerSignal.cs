using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Nightwave.Tuners.Dvb.Transport;

/// <summary>
/// What a SAT>IP tuner says about its signal in the RTCP APP report it sends about five times a
/// second (SAT>IP 1.2.2, RTCP reports): level, lock and quality, the way the front end measures them.
/// </summary>
/// <remarks>
/// The report is a string: <c>ver=1.0;src=1;tuner=2,224,1,15,11836.00,h,dvbs,qpsk,off,0.35,27500,34;pids=0,16,17</c>.
/// The first four fields of <c>tuner</c> are the same for every system: front end, level
/// (0 to 255), lock (0 or 1) and quality (0 to 15); what follows describes the tuning and differs
/// between satellite (<c>ver=1.0</c>), terrestrial (<c>ver=1.1</c>) and cable (<c>ver=1.2</c>).
/// </remarks>
internal sealed record TunerSignal
{
    /// <summary>The name SAT>IP gives its APP packets.</summary>
    public const string AppName = "SES1";

    private const int RtcpApp = 204;

    /// <summary>Signal level, 0 to 255. Zero is no signal at all at this frequency, or not yet.</summary>
    public required int Level { get; init; }

    /// <summary>Whether the demodulator has locked.</summary>
    public required bool Locked { get; init; }

    /// <summary>Signal quality, 0 to 15.</summary>
    public required int Quality { get; init; }

    /// <summary>The input (<c>src</c>) the report is about, for satellite.</summary>
    public int? Source { get; init; }

    /// <summary>The front end of the server that tunes.</summary>
    public int? FrontEnd { get; init; }

    /// <summary>False where the tuner said no level, and <see cref="Level"/> only tells signal from none.</summary>
    public bool Measured { get; init; } = true;

    /// <summary>The level in the tuner's own unit, where that is not the SAT>IP scale.</summary>
    public string? LevelRaw { get; init; }

    /// <summary>The quality in the tuner's own unit, where that is not the SAT>IP scale.</summary>
    public string? QualityRaw { get; init; }

    /// <summary>Carrier to noise in dB. SAT>IP does not report it; a card can.</summary>
    public double? SnrDb { get; init; }

    /// <summary>Errors per bit after correction. SAT>IP does not report it; a card can.</summary>
    public double? BitErrorRate { get; init; }

    /// <summary>Whether the carrier has pilots, where a satellite report says.</summary>
    public bool? Pilots { get; init; }

    /// <summary>For logs and failures: <c>lock, level 224, quality 15</c> or <c>no lock (level 0, quality 0)</c>.</summary>
    public string Describe() => Locked
        ? string.Create(CultureInfo.InvariantCulture, $"lock, level {Level}, quality {Quality}")
        : string.Create(CultureInfo.InvariantCulture, $"no lock (level {Level}, quality {Quality})");

    /// <inheritdoc cref="Describe"/>
    public override string ToString() => Describe();

    /// <summary>Reads the report string. Null when it has no readable <c>tuner</c> field.</summary>
    public static TunerSignal? Parse(string? report)
    {
        if (string.IsNullOrWhiteSpace(report))
        {
            return null;
        }

        int? source = null;
        string[]? tuner = null;

        foreach (var part in report.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            var key = part[..equals];
            var value = part[(equals + 1)..];

            if (key.Equals("src", StringComparison.OrdinalIgnoreCase) && TryInteger(value, out var src))
            {
                source = src;
            }
            else if (key.Equals("tuner", StringComparison.OrdinalIgnoreCase))
            {
                tuner = value.Split(',', StringSplitOptions.TrimEntries);
            }
        }

        if (tuner is not { Length: >= 4 }
            || !TryInteger(tuner[1], out var level)
            || !TryInteger(tuner[2], out var locked)
            || !TryInteger(tuner[3], out var quality))
        {
            return null;
        }

        return new TunerSignal
        {
            FrontEnd = TryInteger(tuner[0], out var frontEnd) ? frontEnd : null,
            Level = Math.Clamp(level, 0, 255),
            Locked = locked == 1,
            Quality = Math.Clamp(quality, 0, 15),
            Source = source,
            Pilots = tuner.Length > 8 && tuner[8] is "on" or "off" ? tuner[8] == "on" : null,
        };
    }

    /// <summary>
    /// Finds the SAT>IP APP report in an RTCP datagram, which is a compound packet (RFC 3550,
    /// 6.1): usually a sender report, a source description and the APP packet, one after the
    /// other. False when there is none, or it cannot be read.
    /// </summary>
    public static bool TryReadRtcp(ReadOnlySpan<byte> datagram, out TunerSignal signal)
    {
        signal = null!;
        var offset = 0;

        while (offset + 4 <= datagram.Length)
        {
            var header = datagram[offset..];

            if ((header[0] >> 6) != 2)
            {
                return false;
            }

            var length = (BinaryPrimitives.ReadUInt16BigEndian(header[2..]) + 1) * 4;
            if (length > header.Length)
            {
                return false;
            }

            // APP: header, SSRC, four characters of name, then SAT>IP's identifier (16 bits),
            // the length of the string (16 bits) and the string.
            if (header[1] == RtcpApp
                && length >= 16
                && header.Slice(8, 4).SequenceEqual("SES1"u8))
            {
                var textLength = BinaryPrimitives.ReadUInt16BigEndian(header[14..]);
                var text = Encoding.ASCII.GetString(header.Slice(16, Math.Min(textLength, length - 16)));

                if (Parse(text) is { } parsed)
                {
                    signal = parsed;
                    return true;
                }

                return false;
            }

            offset += length;
        }

        return false;
    }

    private static bool TryInteger(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}

/// <summary>
/// How a stream is doing: what the tuner last reported about its signal, and what the network
/// delivered. Losses with a good signal are the network's; a weak signal is the dish's or the
/// cable's.
/// </summary>
internal sealed record TransportStatus
{
    /// <summary>For a transport that cannot tell.</summary>
    public static readonly TransportStatus Unknown = new();

    /// <summary>The tuner's last signal report, or null while none has arrived.</summary>
    public TunerSignal? Signal { get; init; }

    /// <summary>How many signal reports arrived; zero from a server that sends none.</summary>
    public long SignalReports { get; init; }

    /// <summary>RTP datagrams received since the tune.</summary>
    public long DatagramsReceived { get; init; }

    /// <summary>RTP datagrams that never arrived, from gaps in their sequence numbers.</summary>
    public long DatagramsLost { get; init; }

    /// <summary>RTP datagrams that came out of order and were put back in place.</summary>
    public long DatagramsReordered { get; init; }

    /// <summary>How the stream travels, once it has been set up.</summary>
    public StreamPath Path { get; init; }
}

/// <summary>How a SAT>IP stream reaches this machine.</summary>
internal enum StreamPath
{
    Unknown,

    /// <summary>RTP inside the RTSP connection, over TCP.</summary>
    RtspInterleaved,

    /// <summary>RTP to a UDP port of this machine, because the server does not interleave.</summary>
    RtpUdp,

    /// <summary>A card in this machine: no network on the way.</summary>
    LocalCard,
}
