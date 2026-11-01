using System.Text;
using Nightwave.Tuners.Dvb.Mpeg.Si;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>
/// Service names in the character tables of EN 300 468, Annex A - the ones a European
/// satellite actually uses, byte by byte.
/// </summary>
public sealed class DvbTextTests
{
    [Fact]
    public void Plain_ascii_is_iso_6937_and_reads_as_it_is()
    {
        Assert.Equal("Das Erste HD", DvbText.Decode("Das Erste HD"u8));
    }

    [Fact]
    public void Iso_6937_puts_the_diacritic_before_the_letter()
    {
        // 0xC8 is the diaeresis, 0xC2 the acute accent, 0xFB the sharp s.
        byte[] text = [0x50, 0xC8, 0x61, 0x72, 0x69, 0x73, 0x20, 0xC2, 0x65, 0x74, 0xC2, 0x65, 0x20, 0x53, 0x74, 0x72, 0x61, 0xFB, 0x65];

        Assert.Equal("Päris été Straße", DvbText.Decode(text));
    }

    [Fact]
    public void Selector_one_is_iso_8859_5()
    {
        // "Россия" in ISO 8859-5.
        byte[] text = [0x01, 0xC0, 0xDE, 0xE1, 0xE1, 0xD8, 0xEF];

        Assert.Equal("Россия", DvbText.Decode(text));
    }

    [Fact]
    public void Selector_three_is_iso_8859_7()
    {
        // "ΕΡΤ" in ISO 8859-7.
        byte[] text = [0x03, 0xC5, 0xD1, 0xD4];

        Assert.Equal("ΕΡΤ", DvbText.Decode(text));
    }

    [Fact]
    public void The_three_byte_selector_names_the_iso_8859_part()
    {
        // 0x10 0x00 0x02 is ISO 8859-2: 0xA9 is Š, 0xE8 is č.
        byte[] text = [0x10, 0x00, 0x02, 0xA9, 0x6B, 0x6F, 0x64, 0x61, 0x20, 0xE8, 0x61, 0x73];

        Assert.Equal("Škoda čas", DvbText.Decode(text));
    }

    [Fact]
    public void Selector_0x11_is_ucs2_big_endian()
    {
        byte[] text = [0x11, .. Encoding.BigEndianUnicode.GetBytes("中央电视台")];

        Assert.Equal("中央电视台", DvbText.Decode(text));
    }

    [Fact]
    public void Selector_0x15_is_utf8()
    {
        byte[] text = [0x15, .. Encoding.UTF8.GetBytes("ZDF — Übertragung")];

        Assert.Equal("ZDF — Übertragung", DvbText.Decode(text));
    }

    [Fact]
    public void Emphasis_codes_are_dropped_and_a_line_break_is_a_space()
    {
        byte[] text = [0x86, 0x52, 0x54, 0x4C, 0x87, 0x8A, 0x48, 0x44];

        Assert.Equal("RTL HD", DvbText.Decode(text));
    }

    [Fact]
    public void Nothing_is_an_empty_name()
    {
        Assert.Equal(string.Empty, DvbText.Decode([]));
    }
}
