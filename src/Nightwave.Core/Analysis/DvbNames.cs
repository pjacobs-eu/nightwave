using System.Globalization;

namespace Nightwave.Core.Analysis;

/// <summary>The names DVB gives to numbers.</summary>
public static class DvbNames
{
    /// <summary>EN 300 468, table 87.</summary>
    public static string ServiceType(int? type) => type switch
    {
        null => "–",
        0x01 => "SD TV (MPEG-2)",
        0x02 => "Radio (MPEG Audio)",
        0x03 => "Teletext",
        0x07 => "Radio (FM)",
        0x0A => "Radio (AAC)",
        0x0C => "Data",
        0x11 => "HD TV (MPEG-2)",
        0x16 => "SD TV (H.264)",
        0x19 => "HD TV (H.264)",
        0x1C => "3D TV",
        0x1F => "UHD TV (HEVC)",
        0x20 => "UHD TV (HEVC)",
        _ => string.Create(CultureInfo.InvariantCulture, $"0x{type:X2}"),
    };

    /// <summary>The conditional access system behind a CA system id (ETR 162), by its range.</summary>
    public static string CaSystem(int id) => (id >> 8) switch
    {
        0x01 => "Seca / Mediaguard",
        0x05 => "Viaccess",
        0x06 => "Irdeto",
        0x09 => "NDS Videoguard",
        0x0B => "Conax",
        0x0D => "Cryptoworks",
        0x0E => "PowerVu",
        0x10 => "RAS",
        0x17 => "BetaCrypt",
        0x18 => "Nagravision",
        0x22 => "Codicrypt",
        0x26 => "BISS",
        0x27 => "Bulcrypt",
        0x4A => "DRE-Crypt",
        0x56 => "Verimatrix",
        _ => "Unknown",
    };

    public static string CaSystemWithId(int id) => string.Create(CultureInfo.InvariantCulture, $"{CaSystem(id)} ({id:X4})");
}

/// <summary>
/// What a satellite carrier can carry by the standard, worked out from how it is tuned. These are
/// the figures of the norm, not measurements.
/// </summary>
public static class CarrierCapacity
{
    /// <summary>EN 302 307-1, table 13 (normal frames, no pilots): spectral efficiency and ideal Es/N0 in dB.</summary>
    private static readonly Dictionary<(string Modulation, string Fec), (double Efficiency, double EsN0)> Dvbs2 = new()
    {
        [("qpsk", "14")] = (0.490243, -2.35),
        [("qpsk", "13")] = (0.656448, -1.24),
        [("qpsk", "25")] = (0.789412, -0.30),
        [("qpsk", "12")] = (0.988858, 1.00),
        [("qpsk", "35")] = (1.188304, 2.23),
        [("qpsk", "23")] = (1.322253, 3.10),
        [("qpsk", "34")] = (1.487473, 4.03),
        [("qpsk", "45")] = (1.587196, 4.68),
        [("qpsk", "56")] = (1.654663, 5.18),
        [("qpsk", "89")] = (1.766451, 6.20),
        [("qpsk", "910")] = (1.788612, 6.42),
        [("8psk", "35")] = (1.779991, 5.50),
        [("8psk", "23")] = (1.980636, 6.62),
        [("8psk", "34")] = (2.228124, 7.91),
        [("8psk", "56")] = (2.478562, 9.35),
        [("8psk", "89")] = (2.646012, 10.69),
        [("8psk", "910")] = (2.679207, 10.98),
        [("16apsk", "23")] = (2.637201, 8.97),
        [("16apsk", "34")] = (2.966728, 10.21),
        [("16apsk", "45")] = (3.165623, 11.03),
        [("16apsk", "56")] = (3.300184, 11.61),
        [("16apsk", "89")] = (3.523143, 12.89),
        [("16apsk", "910")] = (3.567342, 13.13),
        [("32apsk", "34")] = (3.703295, 12.73),
        [("32apsk", "45")] = (3.951571, 13.64),
        [("32apsk", "56")] = (4.119540, 14.28),
        [("32apsk", "89")] = (4.397854, 15.69),
        [("32apsk", "910")] = (4.453027, 16.05),
    };

    /// <summary>Megabits per second of transport stream. Null where modulation or code rate are unknown.</summary>
    public static double? UsefulBitRate(string? system, int? symbolRateKilo, string? modulation, string? fec)
    {
        if (symbolRateKilo is not { } symbols || fec is null)
        {
            return null;
        }

        if (string.Equals(system, "DVB-S", StringComparison.OrdinalIgnoreCase))
        {
            // QPSK, Reed-Solomon 188/204 around the inner code (EN 300 421).
            return Rate(fec) is { } rate ? symbols / 1000d * 2 * rate * 188 / 204 : null;
        }

        return modulation is not null && Dvbs2.TryGetValue((modulation.ToLowerInvariant(), fec), out var entry)
            ? symbols / 1000d * entry.Efficiency
            : null;
    }

    /// <summary>The Es/N0 in dB a DVB-S2 carrier needs in an ideal channel. Null for anything else.</summary>
    public static double? RequiredEsN0(string? system, string? modulation, string? fec) =>
        string.Equals(system, "DVB-S2", StringComparison.OrdinalIgnoreCase)
        && modulation is not null
        && fec is not null
        && Dvbs2.TryGetValue((modulation.ToLowerInvariant(), fec), out var entry)
            ? entry.EsN0
            : null;

    /// <summary>Writes a SAT>IP code rate the way people do: <c>34</c> is <c>3/4</c>.</summary>
    public static string? FecName(string? fec) => fec switch
    {
        null or "" => null,
        { Length: 2 } => $"{fec[0]}/{fec[1]}",
        { Length: 3 } => $"{fec[0]}/{fec[1..]}",
        _ => fec,
    };

    private static double? Rate(string fec) => fec switch
    {
        "12" => 1d / 2,
        "23" => 2d / 3,
        "34" => 3d / 4,
        "56" => 5d / 6,
        "78" => 7d / 8,
        _ => null,
    };
}
