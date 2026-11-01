using System.Globalization;
using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb.Tuning;

namespace Nightwave.Tuners.Dvb.Channels;

/// <summary>
/// A channel a scan found, with everything needed to tune it again without scanning: where it
/// is broadcast, how that transponder is tuned, and the ids and pids it had.
/// </summary>
internal sealed record StoredChannel
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Provider { get; init; }

    /// <summary>Logical channel number from the NIT.</summary>
    public string? Number { get; init; }

    public ChannelKind Kind { get; init; } = ChannelKind.Television;

    /// <summary>The service type from the SDT, where there was one.</summary>
    public int? ServiceType { get; init; }

    /// <summary>Whether the service is scrambled: the SDT said so, or its program map names a CA system.</summary>
    public bool Scrambled { get; init; }

    /// <summary>The conditional access systems the program map named, for the log.</summary>
    public IReadOnlyList<int> CaSystems { get; init; } = [];

    public required DeliverySystem Delivery { get; init; }

    /// <summary>For satellite: the position. Any device with a dish on it can tune the channel.</summary>
    public int? OrbitalPosition { get; init; }

    /// <summary>For cable and terrestrial: the device it was found on.</summary>
    public string? DeviceId { get; init; }

    public TuningParameters? Tuning { get; init; }

    public int OriginalNetworkId { get; init; }

    public int TransportStreamId { get; init; }

    /// <summary>The program number.</summary>
    public int ServiceId { get; init; }

    public int PmtPid { get; init; }

    public int PcrPid { get; init; }

    /// <summary>The elementary stream pids at the time of the scan, so a tune can ask for them before the PMT arrives.</summary>
    public IReadOnlyList<int> Pids { get; init; } = [];

    /// <summary>
    /// The pids of the channel's ECMs at the time of the scan, so a tune of a scrambled channel
    /// can ask for them before the PMT arrives - and has one to hand the card the moment it asks.
    /// </summary>
    public IReadOnlyList<int> EcmPids { get; init; } = [];

    /// <summary>The scan targets that found it. A channel goes when the last of them no longer does.</summary>
    public IReadOnlyList<string> FoundBy { get; init; } = [];

    /// <summary>
    /// The id of a DVB service: where it is received, and the three numbers that identify it in
    /// DVB (EN 300 468, 5.2.3). Stable across scans, and the same whichever device found it.
    /// </summary>
    public static string ServiceKey(string source, int originalNetworkId, int transportStreamId, int serviceId) =>
        string.Create(CultureInfo.InvariantCulture, $"{source}:{originalNetworkId}.{transportStreamId}.{serviceId}");

    /// <summary>Where a satellite service is received: its position, for example <c>19.2E</c>.</summary>
    public static string SatelliteSource(OrbitalPosition position) => position.ToString();

    /// <summary>Where a cable or terrestrial service is received: the network behind that device's input.</summary>
    public static string DeviceSource(DeliverySystem delivery, string deviceId) =>
        $"{(delivery == DeliverySystem.Cable ? "C" : "T")}@{deviceId}";
}
