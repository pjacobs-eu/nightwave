using System.Globalization;
using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb.Channels;
using Nightwave.Tuners.Dvb.Descrambling;
using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.Transport;

namespace Nightwave.Tuners.Dvb.Streaming;

/// <summary>Everything the details of one stream are made of, taken at one moment.</summary>
internal sealed record StreamSnapshot
{
    public required TunerDevice Device { get; init; }

    public required TuneRequest Request { get; init; }

    public required StoredChannel Channel { get; init; }

    /// <summary>The other channels watched from the same tuner.</summary>
    public IReadOnlyList<string> SharedWith { get; init; } = [];

    /// <summary>Tuners of the device in use, this one included.</summary>
    public int TunersInUse { get; init; }

    public TransportStatus Transport { get; init; } = TransportStatus.Unknown;

    /// <summary>Datagrams lost in the last minute, or null before a minute of history.</summary>
    public long? RecentlyLost { get; init; }

    public IReadOnlyCollection<int> Pids { get; init; } = [];

    public bool Scrambled { get; init; }

    public bool CanDescramble { get; init; }

    public DescramblingStatus? Descrambling { get; init; }

    /// <summary>The descrambler's problem as the feed has it, where the status has none.</summary>
    public TunerException? DescramblingProblem { get; init; }

    public long StillScrambledPackets { get; init; }
}

/// <summary>
/// Where a SAT>IP channel comes from and how it is doing, for the player's details panel: the
/// device, the input and transponder, the service, the reception, and - for a scrambled one -
/// the descrambling. Built from what the stream already knows; no I/O.
/// </summary>
/// <remarks>
/// <para>
/// How reception is marked. The level is what the front end reports on SAT>IP's scale of 0 to
/// 255; from 150 up is good, from 80 a warning, below that a problem - under 80 most front ends
/// are near the edge of their lock. The quality, 0 to 15, is good from 10, a warning from 5 and
/// a problem below. No lock is a problem whatever the numbers say.
/// </para>
/// <para>
/// Datagrams lost in the last minute are a warning; losses before that are only counted. Packets
/// still scrambled after the descrambler had them are a warning: a key came late or not at all.
/// </para>
/// </remarks>
internal static class StreamDetails
{
    public const int GoodLevel = 150;
    public const int WeakLevel = 80;
    public const int GoodQuality = 10;
    public const int WeakQuality = 5;

    public static IReadOnlyList<TunedStreamDetailGroup> Build(StreamSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        List<TunedStreamDetailGroup> groups =
        [
            Group("details.device", Device(snapshot)),
            Group("details.input", Input(snapshot)),
            Group("details.service", Service(snapshot)),
            Group("details.reception", Reception(snapshot)),
        ];

        if (snapshot.Scrambled)
        {
            groups.Add(Group("details.descrambling", Descrambling(snapshot)));
        }

        return groups;
    }

    private static IEnumerable<TunedStreamDetail> Device(StreamSnapshot snapshot)
    {
        var device = snapshot.Device;

        yield return Line("details.device.name", device.ReportedName ?? device.Name);

        if (device.ModelName is { Length: > 0 } model)
        {
            yield return Line("details.device.model", model);
        }

        yield return OperatorLine("details.device.address", device.RtspPort == 554 ? device.Host : $"{device.Host}:{Number(device.RtspPort)}");
        yield return Line("details.device.type", device.Capabilities is { Length: > 0 } capabilities ? $"SAT>IP · {capabilities}" : "SAT>IP");
        yield return OperatorLine("details.device.tuners", $"{Number(snapshot.TunersInUse)} / {Number(device.TotalCapacity)}");
        yield return Word("details.device.transport", snapshot.Transport.Path switch
        {
            StreamPath.RtspInterleaved => "details.transport.interleaved",
            StreamPath.RtpUdp => "details.transport.udp",
            _ => "details.transport.unknown",
        });
    }

    private static IEnumerable<TunedStreamDetail> Input(StreamSnapshot snapshot)
    {
        if (snapshot.Request.Source is { } source)
        {
            yield return Line("details.input.source", $"src={Number(source)}");
        }

        if (snapshot.Channel.OrbitalPosition is { } tenths)
        {
            var position = new OrbitalPosition(tenths).ToString();
            yield return Line("details.input.position", $"{position[..^1]}°{position[^1]}");
        }

        yield return Line("details.input.transponder", Transponder(snapshot));

        yield return snapshot.SharedWith.Count == 0
            ? Word("details.input.shared", "details.input.shared.none")
            : Line("details.input.shared", string.Join(", ", snapshot.SharedWith));
    }

    private static IEnumerable<TunedStreamDetail> Service(StreamSnapshot snapshot)
    {
        var channel = snapshot.Channel;

        yield return Line("details.service.name", channel.Name);
        yield return OperatorLine(
            "details.service.ids",
            $"sid {Number(channel.ServiceId)} · tsid {Number(channel.TransportStreamId)} · onid {Number(channel.OriginalNetworkId)}");
        yield return OperatorLine("details.service.pids", string.Join(", ", snapshot.Pids.Order().Select(Number)));
        yield return Word("details.service.scrambled", snapshot.Scrambled ? "details.value.yes" : "details.value.no");
    }

    private static IEnumerable<TunedStreamDetail> Reception(StreamSnapshot snapshot)
    {
        var status = snapshot.Transport;

        if (status.Signal is not { } signal)
        {
            yield return Word("details.reception.lock", "details.reception.no_report");
        }
        else
        {
            yield return Word(
                "details.reception.lock",
                signal.Locked ? "details.reception.locked" : "details.reception.unlocked",
                signal.Locked ? TunedStreamDetailState.Good : TunedStreamDetailState.Problem);
            yield return Line("details.reception.level", $"{Number(signal.Level)} / 255", Grade(signal.Level, GoodLevel, WeakLevel, signal.Locked));
            yield return Line("details.reception.quality", $"{Number(signal.Quality)} / 15", Grade(signal.Quality, GoodQuality, WeakQuality, signal.Locked));
        }

        yield return Line(
            "details.reception.lost",
            $"{Number(status.DatagramsLost)} / {Number(status.DatagramsReceived + status.DatagramsLost)}");

        if (snapshot.RecentlyLost is { } recent)
        {
            yield return Line(
                "details.reception.lost_recent",
                Number(recent),
                recent > 0 ? TunedStreamDetailState.Warning : TunedStreamDetailState.Good);
        }

        if (snapshot.Scrambled)
        {
            yield return Line(
                "details.reception.still_scrambled",
                Number(snapshot.StillScrambledPackets),
                snapshot.StillScrambledPackets > 0 ? TunedStreamDetailState.Warning : TunedStreamDetailState.Neutral);
        }
    }

    private static IEnumerable<TunedStreamDetail> Descrambling(StreamSnapshot snapshot)
    {
        if (!snapshot.CanDescramble)
        {
            yield return Word("details.descrambling.state", "details.descrambling.not_configured", TunedStreamDetailState.Problem);
            yield break;
        }

        var status = snapshot.Descrambling;
        var problem = status?.Problem ?? snapshot.DescramblingProblem;

        if (problem is not null)
        {
            yield return new TunedStreamDetail
            {
                LabelKey = "details.descrambling.state",
                Value = problem.Message,
                ValueKey = "details.descrambling.problem",
                State = TunedStreamDetailState.Problem,
            };
        }
        else if (status is { HasKeys: true })
        {
            yield return Word("details.descrambling.state", "details.descrambling.active", TunedStreamDetailState.Good);
        }
        else
        {
            yield return Word("details.descrambling.state", "details.descrambling.waiting", TunedStreamDetailState.Warning);
        }

        if (status is null)
        {
            yield break;
        }

        yield return OperatorLine(
            "details.descrambling.server",
            status.ServerVersion is { Length: > 0 } version ? $"{status.Server} · {version}" : status.Server,
            status.Connected ? TunedStreamDetailState.Neutral : TunedStreamDetailState.Problem);

        if (status.LastEcm is { } ecm)
        {
            yield return OperatorLine("details.descrambling.caid", string.Create(CultureInfo.InvariantCulture, $"0x{ecm.CaId:X4} ({ecm.CardSystem})"));
            yield return OperatorLine("details.descrambling.reader", ecm.Reader);
            yield return OperatorLine(
                "details.descrambling.source",
                string.Create(CultureInfo.InvariantCulture, $"{ecm.Source} · {ecm.Protocol} · {ecm.Hops} hops"));
            yield return OperatorLine(
                "details.descrambling.ecm_time",
                string.Create(CultureInfo.InvariantCulture, $"{ecm.EcmTimeMilliseconds} ms"),
                ecm.EcmTimeMilliseconds > 1500 ? TunedStreamDetailState.Warning : TunedStreamDetailState.Neutral);
        }

        if (status.Algorithm is { Length: > 0 } algorithm)
        {
            yield return OperatorLine("details.descrambling.algorithm", algorithm);
        }

        yield return Line("details.descrambling.ecms", Number(status.EcmSectionsForwarded));

        if (status.SectionsDropped > 0)
        {
            yield return Line("details.descrambling.dropped", Number(status.SectionsDropped), TunedStreamDetailState.Warning);
        }
    }

    private static string Transponder(StreamSnapshot snapshot)
    {
        var tuning = snapshot.Request.Tuning;
        var parts = new List<string> { tuning.Describe() };

        if (tuning.Modulation is { Length: > 0 } modulation)
        {
            parts.Add(modulation.ToUpperInvariant());
        }

        if (tuning.Fec is { Length: > 1 } fec)
        {
            parts.Add($"FEC {fec[..1]}/{fec[1..]}");
        }

        return string.Join(" · ", parts);
    }

    private static TunedStreamDetailState Grade(int value, int good, int weak, bool locked) =>
        !locked || value < weak
            ? TunedStreamDetailState.Problem
            : value < good ? TunedStreamDetailState.Warning : TunedStreamDetailState.Good;

    private static TunedStreamDetailGroup Group(string titleKey, IEnumerable<TunedStreamDetail> items) =>
        new() { TitleKey = titleKey, Items = [.. items] };

    private static TunedStreamDetail Line(string labelKey, string value, TunedStreamDetailState state = TunedStreamDetailState.Neutral) =>
        new() { LabelKey = labelKey, Value = value, State = state };

    /// <summary>
    /// A line for whoever administers live TV rather than for whoever is watching: addresses on
    /// the network, the card a subscription is descrambled with, the numbers of a transport
    /// stream. A viewer is shown how well their picture comes in, not how the house is wired.
    /// </summary>
    private static TunedStreamDetail OperatorLine(string labelKey, string value, TunedStreamDetailState state = TunedStreamDetailState.Neutral) =>
        new() { LabelKey = labelKey, Value = value, State = state, IsOperatorOnly = true };

    private static TunedStreamDetail Word(string labelKey, string valueKey, TunedStreamDetailState state = TunedStreamDetailState.Neutral) =>
        new() { LabelKey = labelKey, Value = string.Empty, ValueKey = valueKey, State = state };

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
