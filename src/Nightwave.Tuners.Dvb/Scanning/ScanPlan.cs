using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb.Channels;
using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.Mpeg.Si;
using Nightwave.Tuners.Dvb.Tuning;

namespace Nightwave.Tuners.Dvb.Scanning;

/// <summary>
/// One scannable input: a device and what it receives. The target id is what the admin UI
/// passes back to start a scan, so it is built from things that stay the same.
/// </summary>
internal sealed record ScanInput
{
    public required string TargetId { get; init; }

    public required TunerDevice Device { get; init; }

    public required DeliverySystem Delivery { get; init; }

    /// <summary>The SAT>IP input, for satellite.</summary>
    public int? Source { get; init; }

    public OrbitalPosition? Position { get; init; }

    /// <summary>English reason when this input cannot be scanned as configured.</summary>
    public string? Error { get; init; }

    /// <remarks>
    /// Made only of what reads the same in every language - the device, the position, the
    /// system and SAT>IP's own name for the input - because the contract hands it out untranslated.
    /// </remarks>
    public string Name => Delivery switch
    {
        DeliverySystem.Satellite when Position is { } position =>
            $"{Device.Label} - {Format(position)} (src={Source})",
        DeliverySystem.Satellite => $"{Device.Label} - DVB-S",
        DeliverySystem.Cable => $"{Device.Label} - DVB-C",
        DeliverySystem.Terrestrial => $"{Device.Label} - DVB-T/T2",
        _ => Device.Label,
    };

    /// <summary>The key channels found on this input are stored under: the position, or the device's network.</summary>
    public string ChannelSource => Delivery == DeliverySystem.Satellite && Position is { } position
        ? StoredChannel.SatelliteSource(position)
        : StoredChannel.DeviceSource(Delivery, Device.Id);

    /// <summary>The transponder the input detection locked on, where it did: the surest place to start.</summary>
    public TuningParameters? LockedOn { get; init; }

    /// <summary>
    /// Every input of every device, including the ones that cannot be scanned yet, with the
    /// reason. One target per satellite: where several inputs of a device receive the same
    /// position, the one with the lowest number stands for them, since they carry the same
    /// channels.
    /// </summary>
    public static IReadOnlyList<ScanInput> From(IEnumerable<TunerDevice> devices)
    {
        var inputs = new List<ScanInput>();

        foreach (var device in devices)
        {
            var satellites = device.SatelliteInputs.OrderBy(input => input.Source).DistinctBy(input => input.Position).ToList();

            if (satellites.Count > 0)
            {
                inputs.AddRange(satellites.Select(input => new ScanInput
                {
                    TargetId = SatelliteTargetId(device, input.Source),
                    Device = device,
                    Delivery = DeliverySystem.Satellite,
                    Source = input.Source,
                    Position = input.Position,
                    LockedOn = input.LockedOn,
                    Error = device.TunersKnown && device.Capacity(DeliverySystem.Satellite) == 0
                        ? $"{device.Label} reports no satellite tuners."
                        : null,
                }));
            }
            else if (device.Capacity(DeliverySystem.Satellite) > 0)
            {
                inputs.Add(new ScanInput
                {
                    TargetId = $"{device.Id}/satellite",
                    Device = device,
                    Delivery = DeliverySystem.Satellite,
                    Error = device.InputsProblem ?? $"No satellite was found on any input of {device.Label}.",
                });
            }

            // Only where the device says it has such tuners: every SAT>IP server that could not
            // be asked would otherwise offer an aerial and a cable it most likely does not have.
            foreach (var delivery in new[] { DeliverySystem.Cable, DeliverySystem.Terrestrial })
            {
                if (device.TunersKnown && device.Capacity(delivery) > 0)
                {
                    inputs.Add(new ScanInput
                    {
                        TargetId = $"{device.Id}/{(delivery == DeliverySystem.Cable ? "cable" : "terrestrial")}",
                        Device = device,
                        Delivery = delivery,
                    });
                }
            }
        }

        return inputs;
    }

    /// <summary>The target of one satellite input. The same as earlier versions used, so what they scanned keeps its target.</summary>
    public static string SatelliteTargetId(TunerDevice device, int source) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{device.Id}/src{source}");

    /// <summary>
    /// Where a satellite scan starts: the transponder the detection locked on, then the table of
    /// the position. Cable and terrestrial scans have no list; they search (see <see cref="BlindScan"/>).
    /// </summary>
    public IReadOnlyList<IReadOnlyList<TuningParameters>> StartTransponders()
    {
        if (Delivery != DeliverySystem.Satellite || Position is not { } position)
        {
            return [];
        }

        var start = new List<TuningParameters>();

        if (LockedOn is { } locked)
        {
            start.Add(locked);
        }

        start.AddRange(InitialTransponders.For(position).Where(tuning => !start.Any(known => known.IsSameTransponder(tuning))));

        return [.. start.Select(tuning => (IReadOnlyList<TuningParameters>)[tuning])];
    }

    /// <summary>
    /// Whether a transport stream from a NIT belongs to this scan: on the same satellite, or in
    /// the same cable network. A terrestrial NIT lists transmitters that may be out of reach, so
    /// terrestrial scans stay with the multiplexes that locked.
    /// </summary>
    public bool Follows(NetworkTransportStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return Delivery switch
        {
            DeliverySystem.Satellite => stream.Tuning?.Delivery == DeliverySystem.Satellite
                && stream.OrbitalPosition is { } orbital
                && Position is { } position
                && Math.Abs(orbital - position.Tenths) <= 1,
            DeliverySystem.Cable => stream.Tuning?.Delivery == DeliverySystem.Cable,
            _ => false,
        };
    }

    private static string Format(OrbitalPosition position)
    {
        var text = position.ToString();

        return $"{text[..^1]}°{text[^1]}";
    }
}
