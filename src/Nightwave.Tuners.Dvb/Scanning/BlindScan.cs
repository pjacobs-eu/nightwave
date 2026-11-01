using Nightwave.Tuners.Dvb.Tuning;

namespace Nightwave.Tuners.Dvb.Scanning;

/// <summary>
/// Where a cable or terrestrial scan looks when nobody told it any frequency: the channel
/// raster of the European cable and UHF plans, tried with the settings networks actually use.
/// </summary>
/// <remarks>
/// A cable scan needs only one transponder that locks and carries a NIT: from there the network
/// lists the rest, as on satellite. A terrestrial NIT lists transmitters that may be out of
/// reach, so an aerial is searched channel by channel and every multiplex that locks is read.
/// </remarks>
internal static class BlindScan
{
    /// <summary>
    /// The 8 MHz cable raster, 114 to 858 MHz. Every 8 MHz channel centre of the European
    /// plans (the special channels, the hyperband and UHF 21 to 69) lies on it. The hyperband
    /// and UHF come first, where digital cable is densest; the lower special channels, often
    /// taken by analogue radio and cable modems, come last - followed by the band III centres
    /// of the 7 MHz plan (channels 5 to 12), which a few networks use for DVB-C as well.
    /// </summary>
    public static IReadOnlyList<int> CableFrequenciesKhz { get; } =
    [
        .. Enumerable.Range(0, 70).Select(step => (306 + (8 * step)) * 1000),
        .. Enumerable.Range(0, 24).Select(step => (114 + (8 * step)) * 1000),
        .. Enumerable.Range(0, 8).Select(step => 177500 + (7000 * step)),
    ];

    /// <summary>
    /// Symbol rate and modulation of European cable networks: 6900 kS/s with 256-QAM in most of
    /// them, 6875 kS/s in the Netherlands, Belgium and elsewhere, 64-QAM on older transponders.
    /// </summary>
    public static IReadOnlyList<(int SymbolRate, string Modulation)> CableSettings { get; } =
    [
        (6900, "256qam"),
        (6875, "256qam"),
        (6900, "64qam"),
        (6875, "64qam"),
    ];

    /// <summary>UHF channels 21 to 48: 474 to 690 MHz, 8 MHz apart, where DVB-T and DVB-T2 are broadcast in Europe.</summary>
    public static IReadOnlyList<int> TerrestrialFrequenciesKhz { get; } =
        [.. Enumerable.Range(21, 28).Select(channel => (474 + (8 * (channel - 21))) * 1000)];

    public static TuningParameters Cable(int frequencyKhz, (int SymbolRate, string Modulation) setting) => new()
    {
        System = DvbSystem.DvbC,
        FrequencyKhz = frequencyKhz,
        SymbolRate = setting.SymbolRate,
        Modulation = setting.Modulation,
    };

    /// <summary>DVB-T2 first: a T2 multiplex does not lock as DVB-T, and T2 is what most of Europe has moved to.</summary>
    public static IReadOnlyList<TuningParameters> Terrestrial(int frequencyKhz) =>
    [
        new TuningParameters { System = DvbSystem.DvbT2, FrequencyKhz = frequencyKhz, Bandwidth = "8", Plp = 0 },
        new TuningParameters { System = DvbSystem.DvbT, FrequencyKhz = frequencyKhz, Bandwidth = "8" },
    ];
}

/// <summary>
/// The order cable settings are tried in: the one that locked last goes first. A network uses one
/// symbol rate and modulation on nearly every transponder, so after the first lock each
/// frequency is usually settled with one try.
/// </summary>
internal sealed class CableSettingsOrder
{
    private readonly List<(int SymbolRate, string Modulation)> _order = [.. BlindScan.CableSettings];

    public IReadOnlyList<(int SymbolRate, string Modulation)> Current => [.. _order];

    public void Locked((int SymbolRate, string Modulation) setting)
    {
        if (_order.Remove(setting))
        {
            _order.Insert(0, setting);
        }
    }
}
