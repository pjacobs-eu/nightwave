using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Nightwave.Core.Tuning;

namespace Nightwave.Tuners.Dvb.Tuning;

/// <summary>The broadcast systems a SAT>IP server can tune.</summary>
internal enum DvbSystem
{
    DvbS,
    DvbS2,
    DvbC,
    DvbT,
    DvbT2,
}

internal enum Polarization
{
    Horizontal,
    Vertical,
    CircularLeft,
    CircularRight,
}

/// <summary>
/// Everything needed to tune one transponder, in the terms SAT>IP uses (SAT>IP 1.2.2, 3.5.11):
/// frequencies as the query carries them, modulation and code rate spelt the way the query
/// spells them. Stored with every channel, so a channel can be tuned again months later without
/// a scan.
/// </summary>
/// <remarks>
/// What the tuning does not include is the input of the device: a satellite channel is tied to
/// an orbital position, and any device with a dish on that position can tune it. The input
/// number is added when a device is picked.
/// </remarks>
internal sealed record TuningParameters
{
    public required DvbSystem System { get; init; }

    /// <summary>
    /// For satellite the downlink frequency, which is what SAT>IP expects rather than the
    /// intermediate frequency behind the LNB; the centre frequency for cable and terrestrial.
    /// </summary>
    public required int FrequencyKhz { get; init; }

    public Polarization? Polarization { get; init; }

    /// <summary>Kilosymbols per second, for satellite and cable.</summary>
    public int? SymbolRate { get; init; }

    /// <summary><c>qpsk</c>, <c>8psk</c>, <c>16apsk</c>, <c>32apsk</c>, <c>16qam</c> up to <c>256qam</c>.</summary>
    public string? Modulation { get; init; }

    /// <summary>Inner code rate without the slash: <c>34</c> is 3/4, <c>910</c> is 9/10.</summary>
    public string? Fec { get; init; }

    /// <summary><c>0.35</c>, <c>0.25</c> or <c>0.20</c>; DVB-S has 0.35 only.</summary>
    public string? RollOff { get; init; }

    public bool? Pilots { get; init; }

    /// <summary>Channel bandwidth in MHz for terrestrial: <c>8</c>, <c>7</c>, <c>6</c>, <c>5</c>, <c>10</c>, <c>1.712</c>.</summary>
    public string? Bandwidth { get; init; }

    /// <summary><c>2k</c>, <c>8k</c> and so on for terrestrial, where known.</summary>
    public string? TransmissionMode { get; init; }

    /// <summary><c>14</c> is 1/4, <c>132</c> is 1/32 and so on, where known.</summary>
    public string? GuardInterval { get; init; }

    /// <summary>The physical layer pipe of a DVB-T2 multiplex.</summary>
    public int? Plp { get; init; }

    public int? T2SystemId { get; init; }

    [JsonIgnore]
    public DeliverySystem Delivery => System switch
    {
        DvbSystem.DvbS or DvbSystem.DvbS2 => DeliverySystem.Satellite,
        DvbSystem.DvbC => DeliverySystem.Cable,
        _ => DeliverySystem.Terrestrial,
    };

    /// <summary>The frequency the way SAT>IP writes it: MHz, with decimals only where there are any.</summary>
    [JsonIgnore]
    public string FrequencyMhz => (FrequencyKhz / 1000m).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// Whether two tunings reach the same transponder. Frequencies from a NIT and from a table are
    /// often a few hundred kilohertz apart for the same transponder, and tuning it twice would
    /// only find everything twice.
    /// </summary>
    public bool IsSameTransponder(TuningParameters other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (Delivery != other.Delivery || Polarization != other.Polarization || (Plp ?? 0) != (other.Plp ?? 0))
        {
            return false;
        }

        var tolerance = Delivery == DeliverySystem.Satellite ? 2000 : 1000;

        return Math.Abs(FrequencyKhz - other.FrequencyKhz) <= tolerance;
    }

    /// <inheritdoc cref="Describe"/>
    public override string ToString() => Describe();

    /// <summary>As a receiver would show it: <c>11836 MHz H 27500 DVB-S</c>.</summary>
    public string Describe()
    {
        var builder = new StringBuilder();
        builder.Append(FrequencyMhz).Append(" MHz");

        if (Polarization is { } polarization)
        {
            builder.Append(' ').Append(polarization switch
            {
                Tuning.Polarization.Horizontal => 'H',
                Tuning.Polarization.Vertical => 'V',
                Tuning.Polarization.CircularLeft => 'L',
                _ => 'R',
            });
        }

        if (SymbolRate is { } symbolRate)
        {
            builder.Append(' ').Append(symbolRate.ToString(CultureInfo.InvariantCulture));
        }

        if (Bandwidth is { } bandwidth)
        {
            builder.Append(' ').Append(bandwidth).Append(" MHz");
        }

        builder.Append(' ').Append(System switch
        {
            DvbSystem.DvbS => "DVB-S",
            DvbSystem.DvbS2 => "DVB-S2",
            DvbSystem.DvbC => "DVB-C",
            DvbSystem.DvbT => "DVB-T",
            _ => "DVB-T2",
        });

        if (Plp is { } plp && System == DvbSystem.DvbT2)
        {
            builder.Append(" PLP ").Append(plp.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
