using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb.LinuxDvb;
using Nightwave.Tuners.Dvb.Tuning;

namespace Nightwave.Tuners.Dvb.Devices;

/// <summary>
/// A device as the plugin uses it: what was found on the network, merged with what the
/// operator wrote down about it and what the plugin detected on its inputs.
/// </summary>
internal sealed record TunerDevice
{
    private const int DefaultInputCount = 4;

    /// <summary>Stable id: <c>satip:</c> and the server's address.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Host name or IP address, without a port.</summary>
    public required string Host { get; init; }

    public int RtspPort { get; init; } = 554;

    /// <summary>The name the device gives itself, where it could be asked; <see cref="Name"/> may be the operator's.</summary>
    public string? ReportedName { get; init; }

    /// <summary>The maker from the device description.</summary>
    public string? Manufacturer { get; init; }

    /// <summary>The card in this machine this device is, where it is not on the network.</summary>
    public LocalDvbCard? Card { get; init; }

    /// <summary>
    /// A FRITZ!Box with cable tuners. Asked for a stream without pids it does not answer, and it
    /// does not know <c>addpids</c> and <c>delpids</c>.
    /// </summary>
    public bool IsFritzBox =>
        Manufacturer?.Contains("AVM", StringComparison.OrdinalIgnoreCase) == true
        && ModelName?.Contains("FRITZ!", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>How many inputs detection tries.</summary>
    public int InputCount => Card?.Dish.Inputs ?? DefaultInputCount;

    /// <summary>Longer a tune may take here than elsewhere: a motor has to get there first.</summary>
    public TimeSpan TuneAllowance => Card?.Dish.Rotor is null or Core.Configuration.DvbRotor.None ? TimeSpan.Zero : TimeSpan.FromSeconds(90);

    /// <summary>The model from the device description, for example <c>EXIP-418</c>.</summary>
    public string? ModelName { get; init; }

    /// <summary><c>X_SATIPCAP</c> as the device wrote it, for example <c>DVBS2-8</c>.</summary>
    public string? Capabilities { get; init; }

    /// <summary>Tuners the device says it has, per kind of signal.</summary>
    public IReadOnlyDictionary<DeliverySystem, int> Tuners { get; init; } =
        new Dictionary<DeliverySystem, int>();

    /// <summary>False when the device could not be asked, so <see cref="Tuners"/> is a guess.</summary>
    public bool TunersKnown { get; init; }

    /// <summary>The operator's limit. Zero is none.</summary>
    public int MaxTuners { get; init; }

    /// <summary>The satellites detected on the device's inputs, from <see cref="InputCache"/>.</summary>
    public IReadOnlyList<SatelliteInput> SatelliteInputs { get; init; } = [];

    /// <summary>English reason why no satellite input is known yet, where none is.</summary>
    public string? InputsProblem { get; init; }

    /// <summary>English, untranslated reason when the device cannot be used right now.</summary>
    public string? Error { get; init; }

    /// <summary>How the device is named in a target, a tuner status and an error: its name and address.</summary>
    public string Label => string.Equals(Name, Host, StringComparison.OrdinalIgnoreCase) ? Name : $"{Name} ({Host})";

    /// <summary>
    /// How many tuners may be used for one kind of signal. A device that could not be asked is
    /// believed to have what the operator allowed, or one, for whatever it is asked to tune:
    /// channels it found before keep working, and a scan finds out what it really receives.
    /// </summary>
    public int Capacity(DeliverySystem delivery)
    {
        var reported = TunersKnown
            ? Tuners.GetValueOrDefault(delivery)
            : delivery == DeliverySystem.Network ? 0 : Math.Max(MaxTuners, 1);

        return MaxTuners > 0 ? Math.Min(reported, MaxTuners) : reported;
    }

    /// <summary>How many tuners may be used at once, whatever they tune.</summary>
    public int TotalCapacity
    {
        get
        {
            var sum = TunersKnown ? Tuners.Values.Sum() : Math.Max(MaxTuners, 1);

            return MaxTuners > 0 ? Math.Min(sum, MaxTuners) : sum;
        }
    }
}

/// <summary>One input of a SAT>IP server and the satellite its dish points at.</summary>
/// <param name="Source">The SAT>IP input, <c>src</c>.</param>
/// <param name="Position">The orbital position, as the network on it says.</param>
internal sealed record SatelliteInput(int Source, OrbitalPosition Position)
{
    /// <summary>The transponder the detection locked on, a good place for a scan to start.</summary>
    public TuningParameters? LockedOn { get; init; }
}
