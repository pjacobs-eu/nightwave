namespace Nightwave.Tuners.Dvb.Mpeg.Psi;

/// <summary>
/// The long form header every table here uses (ISO/IEC 13818-1, 2.4.4.10): table id, the
/// table id extension (transport stream id, program number, network id - depending on the
/// table), version, and which of how many sections this one is.
/// </summary>
internal readonly record struct SectionHeader(
    int TableId,
    int TableIdExtension,
    int Version,
    bool IsCurrent,
    int SectionNumber,
    int LastSectionNumber)
{
    /// <summary>Header, eight bytes, plus the CRC at the end.</summary>
    public const int Overhead = 8 + 4;

    /// <summary>
    /// Reads the header of a complete section. False for a section that is too short or does
    /// not use the long form, which none of the tables read here may.
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> section, out SectionHeader header)
    {
        header = default;

        if (section.Length < Overhead || (section[1] & 0x80) == 0)
        {
            return false;
        }

        var declared = 3 + (((section[1] & 0x0F) << 8) | section[2]);
        if (declared != section.Length)
        {
            return false;
        }

        header = new SectionHeader(
            section[0],
            (section[3] << 8) | section[4],
            (section[5] >> 1) & 0x1F,
            (section[5] & 0x01) != 0,
            section[6],
            section[7]);

        return true;
    }

    /// <summary>What follows the header and precedes the CRC.</summary>
    public static ReadOnlySpan<byte> Body(ReadOnlySpan<byte> section) => section[8..^4];
}
