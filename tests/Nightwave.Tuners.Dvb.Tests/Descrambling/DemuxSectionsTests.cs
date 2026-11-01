using Nightwave.Tuners.Dvb.Descrambling;

namespace Nightwave.Tuners.Dvb.Tests.Descrambling;

/// <summary>Section filters the way a Linux demux applies them, and sections put back together from packets.</summary>
public class DemuxSectionsTests
{
    private static DemuxFilter Filter(byte[] data, byte[] mask, byte[]? mode = null, uint flags = 0) =>
        new(0, 0, Ts.EcmPid, Pad(data), Pad(mask), Pad(mode ?? []), flags);

    private static byte[] Pad(byte[] bytes)
    {
        var padded = new byte[16];
        bytes.CopyTo(padded, 0);
        return padded;
    }

    [Fact]
    public void The_table_id_is_matched_under_the_mask()
    {
        var ecms = Filter([0x80], [0xFE]);

        Assert.True(ecms.Matches(Ts.Ecm(0x80, 1)));
        Assert.True(ecms.Matches(Ts.Ecm(0x81, 1)));
        Assert.False(ecms.Matches(Ts.Ecm(0x82, 1)));
    }

    /// <summary>Filter byte 1 is section byte 3: the demux skips the two length bytes.</summary>
    [Fact]
    public void Filter_bytes_after_the_first_skip_the_length()
    {
        var section = Ts.Ecm(0x80, 1);
        var filter = Filter([0x80, section[3], section[4]], [0xFF, 0xFF, 0xFF]);
        var wrong = Filter([0x80, section[1]], [0xFF, 0xFF]);

        Assert.True(filter.Matches(section));
        Assert.False(wrong.Matches(section));
    }

    [Fact]
    public void Masked_bits_with_mode_set_have_to_differ()
    {
        // The server's "not the ECM I just had" filter, in Linux demux terms.
        var notEven = Filter([0x80], [0xFF], [0xFF]);

        Assert.False(notEven.Matches(Ts.Ecm(0x80, 1)));
        Assert.True(notEven.Matches(Ts.Ecm(0x81, 1)));
    }

    [Fact]
    public void Crc_checking_filters_drop_damaged_sections()
    {
        var section = Ts.Section(0x01, [0xFF, 0xFF, 0xC1, 0x00, 0x00, 0x09, 0x04, 0x09, 0x63, 0xE6, 0x00]);
        var cat = Filter([0x01], [0xFF], flags: DvbApiProtocol.FilterFlagCheckCrc);

        Assert.True(cat.Matches(section));
        section[8] ^= 1;
        Assert.False(cat.Matches(section));
    }

    [Fact]
    public void A_section_over_several_packets_comes_out_whole()
    {
        var large = Ts.Section(0x82, [.. Enumerable.Range(0, 500).Select(i => (byte)i)]);
        var continuity = 0;
        var packets = Ts.Packets(Ts.EcmPid, ref continuity, large);
        Assert.Equal(3, packets.Length / 188);

        var sections = Assemble(packets);

        Assert.Equal([large], sections);
    }

    [Fact]
    public void Several_sections_in_one_packet_all_come_out()
    {
        var first = Ts.Ecm(0x80, 1)[..20];
        first[2] = 17;
        var second = Ts.Ecm(0x81, 2)[..30];
        second[2] = 27;

        var packet = new byte[188];
        Array.Fill(packet, (byte)0xFF);
        packet[0] = 0x47;
        packet[1] = 0x40 | (Ts.EcmPid >> 8);
        packet[2] = Ts.EcmPid & 0xFF;
        packet[3] = 0x10;
        packet[4] = 0;
        first.CopyTo(packet, 5);
        second.CopyTo(packet, 25);

        Assert.Equal([first, second], Assemble(packet));
    }

    [Fact]
    public void A_lost_packet_drops_the_section_it_was_part_of()
    {
        var large = Ts.Section(0x82, new byte[400]);
        var next = Ts.Ecm(0x80, 3);
        var continuity = 0;
        var packets = Ts.Packets(Ts.EcmPid, ref continuity, large, next);

        // Without the second packet of the large section.
        var damaged = packets[..188].Concat(packets[376..]).ToArray();

        Assert.Equal([next], Assemble(damaged));
    }

    [Fact]
    public void A_repeated_packet_is_ignored()
    {
        var large = Ts.Section(0x82, new byte[300]);
        var continuity = 0;
        var packets = Ts.Packets(Ts.EcmPid, ref continuity, large);
        var repeated = packets[..188].Concat(packets[..188]).Concat(packets[188..]).ToArray();

        Assert.Equal([large], Assemble(repeated));
    }

    [Fact]
    public void A_section_continued_under_a_pointer_field_is_finished_first()
    {
        var first = Ts.Section(0x82, new byte[200]);
        var second = Ts.Ecm(0x81, 9);

        // Packet 1 starts the first section; packet 2 finishes it and starts the second.
        var packet1 = new byte[188];
        packet1[0] = 0x47;
        packet1[1] = 0x40 | (Ts.EcmPid >> 8);
        packet1[2] = Ts.EcmPid & 0xFF;
        packet1[3] = 0x10;
        packet1[4] = 0;
        first.AsSpan(0, 183).CopyTo(packet1.AsSpan(5));

        var rest = first.Length - 183;
        var packet2 = new byte[188];
        Array.Fill(packet2, (byte)0xFF);
        packet2[0] = 0x47;
        packet2[1] = 0x40 | (Ts.EcmPid >> 8);
        packet2[2] = Ts.EcmPid & 0xFF;
        packet2[3] = 0x11;
        packet2[4] = (byte)rest;
        first.AsSpan(183).CopyTo(packet2.AsSpan(5));
        second.CopyTo(packet2, 5 + rest);

        Assert.Equal([first, second], Assemble([.. packet1, .. packet2]));
    }

    private static List<byte[]> Assemble(byte[] packets)
    {
        var assembler = new DemuxSectionAssembler();
        var sections = new List<byte[]>();
        for (var offset = 0; offset < packets.Length; offset += 188)
        {
            assembler.Feed(packets.AsSpan(offset, 188), sections, static (section, list) => list.Add(section.ToArray()));
        }

        return sections;
    }
}
