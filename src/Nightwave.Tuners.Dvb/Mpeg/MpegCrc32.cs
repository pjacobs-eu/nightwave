namespace Nightwave.Tuners.Dvb.Mpeg;

/// <summary>
/// The CRC that closes every PSI and SI section (ISO/IEC 13818-1, Annex A): polynomial
/// 0x04C11DB7, register preset to all ones, bits taken most significant first, nothing
/// reflected and nothing inverted at the end. That last part is why it is written here rather
/// than taken from a CRC-32 library - the Ethernet CRC every library offers uses the same
/// polynomial but reflects it, and gives a different answer.
/// </summary>
internal static class MpegCrc32
{
    private static readonly uint[] Table = CreateTable();

    /// <summary>
    /// The CRC of <paramref name="data"/>. Run over a whole section including its own CRC, the
    /// result is zero when the section arrived intact - which is how the standard means it to be
    /// checked.
    /// </summary>
    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var value in data)
        {
            crc = (crc << 8) ^ Table[((crc >> 24) ^ value) & 0xFF];
        }

        return crc;
    }

    /// <summary>Whether a section, CRC included, arrived intact.</summary>
    public static bool IsValid(ReadOnlySpan<byte> section) => section.Length >= 4 && Compute(section) == 0;

    /// <summary>Computes the CRC over everything but the last four bytes and writes it there.</summary>
    public static void Seal(Span<byte> section)
    {
        var crc = Compute(section[..^4]);

        section[^4] = (byte)(crc >> 24);
        section[^3] = (byte)(crc >> 16);
        section[^2] = (byte)(crc >> 8);
        section[^1] = (byte)crc;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];

        for (var index = 0u; index < 256; index++)
        {
            var crc = index << 24;

            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80000000) != 0 ? (crc << 1) ^ 0x04C11DB7 : crc << 1;
            }

            table[index] = crc;
        }

        return table;
    }
}
