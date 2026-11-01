using Nightwave.Tuners.Dvb.Tuning;

namespace Nightwave.Tuners.Dvb.Scanning;

/// <summary>
/// Where a satellite scan starts: a few transponders per position, from whose NIT the scan
/// learns every other transponder. More than one per position, so one that moved does not stop
/// the scan.
/// </summary>
/// <remarks>
/// Frequency, polarization, system, modulation, symbol rate and code rate of every entry were
/// checked against the LinuxTV dtv-scan-tables (https://git.linuxtv.org/dtv-scan-tables.git,
/// files dvb-s/Astra-1N-19.2E, Astra-1P-19.2E, Hot-Bird-13G-13.0E, Astra-3C-23.5E and
/// Astra-2E-28.2E, which cite KingOfSat and LyngSat) in September 2026. Among them are transponders those tables label as ARD Digital and RTL Group on 19.2E
/// and as M7 Group and Skylink on 23.5E.
/// </remarks>
internal static class InitialTransponders
{
    private static readonly Dictionary<int, TuningParameters[]> Table = new()
    {
        [192] =
        [
            DvbS(11836, Polarization.Horizontal, 27500, "34"),
            DvbS2(11494, Polarization.Horizontal, 22000, "8psk", "23"),
            DvbS(12188, Polarization.Horizontal, 27500, "34"),
        ],
        [130] =
        [
            DvbS(11034, Polarization.Vertical, 27500, "34"),
            DvbS2(11747, Polarization.Horizontal, 27500, "8psk", "34"),
            DvbS(10992, Polarization.Vertical, 27500, "23"),
        ],
        [235] =
        [
            DvbS2(11856, Polarization.Vertical, 29900, "8psk", "34"),
            DvbS(12070, Polarization.Horizontal, 27500, "34"),
        ],
        [282] =
        [
            DvbS2(10773, Polarization.Horizontal, 23000, "8psk", "34"),
            DvbS2(10847, Polarization.Vertical, 23000, "8psk", "34"),
            DvbS(11836, Polarization.Horizontal, 27500, "56"),
        ],
    };

    /// <summary>The positions there is a table for, in the order a detection tries them: the most watched first.</summary>
    public static IReadOnlyList<OrbitalPosition> Positions { get; } = [new(192), new(130), new(282), new(235)];

    /// <summary>
    /// The start transponders for a position, or none where this plugin has no table for it. A
    /// tenth of a degree either way is the same satellite: networks and tables round differently.
    /// </summary>
    public static IReadOnlyList<TuningParameters> For(OrbitalPosition position) =>
        Table.TryGetValue(position.Tenths, out var exact)
            ? exact
            : Table.Where(entry => Math.Abs(entry.Key - position.Tenths) <= 1).Select(entry => entry.Value).FirstOrDefault() ?? [];

    private static TuningParameters DvbS(int mhz, Polarization polarization, int symbolRate, string fec) => new()
    {
        System = DvbSystem.DvbS,
        FrequencyKhz = mhz * 1000,
        Polarization = polarization,
        SymbolRate = symbolRate,
        Modulation = "qpsk",
        RollOff = "0.35",
        Fec = fec,
    };

    private static TuningParameters DvbS2(int mhz, Polarization polarization, int symbolRate, string modulation, string fec) => new()
    {
        System = DvbSystem.DvbS2,
        FrequencyKhz = mhz * 1000,
        Polarization = polarization,
        SymbolRate = symbolRate,
        Modulation = modulation,
        RollOff = "0.35",
        Fec = fec,
    };
}
