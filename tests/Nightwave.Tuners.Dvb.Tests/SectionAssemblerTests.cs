using Nightwave.Tuners.Dvb.Mpeg;
using Nightwave.Tuners.Dvb.Mpeg.Psi;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>
/// Sections arrive cut into packets however the multiplexer liked: several to a packet, one
/// over several, the start of one behind the end of another. Every way of cutting has to give
/// back the same sections, and a damaged one none at all.
/// </summary>
public sealed class SectionAssemblerTests
{
    [Fact]
    public void The_mpeg_crc_is_the_unreflected_one()
    {
        // The CRC-32/MPEG-2 check value: "123456789" gives 0x0376E6E7. The Ethernet CRC every
        // library offers gives 0xCBF43926 for the same input, and would reject every section.
        Assert.Equal(0x0376E6E7u, MpegCrc32.Compute("123456789"u8));
    }

    [Fact]
    public void A_sealed_section_checks_and_a_changed_one_does_not()
    {
        var section = TsBuilder.Pat(0x0401, 3, (28006, 0x0064));

        Assert.True(MpegCrc32.IsValid(section));

        section[9] ^= 0x01;

        Assert.False(MpegCrc32.IsValid(section));
    }

    [Fact]
    public void A_section_in_one_packet_comes_back_whole()
    {
        var section = TsBuilder.Pat(0x0401, 0, (28006, 0x0064), (28007, 0x0065));
        var found = Assemble(TsBuilder.Packets(section, 0));

        Assert.Equal(section, Assert.Single(found));
    }

    [Fact]
    public void A_section_over_several_packets_comes_back_whole()
    {
        // Sixty streams make a program map of well over one packet.
        var streams = Enumerable.Range(0, 60).Select(index => (0x04, 0x100 + index, TsBuilder.Descriptor(0x0A, (byte)'d', (byte)'e', (byte)'u', 0))).ToArray();
        var section = TsBuilder.Pmt(1, 0, 0x100, [], streams);
        var packets = TsBuilder.Packets(section, 0x50);

        Assert.True(packets.Count > 3);
        Assert.Equal(section, Assert.Single(Assemble(packets)));
    }

    [Fact]
    public void Two_sections_in_one_packet_and_the_start_of_a_third_behind_a_pointer()
    {
        var first = TsBuilder.Section(0x42, 1, 0, [0, 1, 0xFF], number: 0, last: 2);
        var second = TsBuilder.Section(0x42, 1, 0, [0, 1, 0xFF], number: 1, last: 2);
        var third = TsBuilder.Section(0x42, 1, 0, [.. Enumerable.Repeat((byte)0x55, 300)], number: 2, last: 2);

        // Packet one: pointer 0, the first two sections, then the start of the third.
        var payload = new List<byte> { 0 };
        payload.AddRange(first);
        payload.AddRange(second);
        payload.AddRange(third.Take(184 - payload.Count));
        var consumed = payload.Count - 1 - first.Length - second.Length;

        var one = Packet(0x11, 0, start: true, payload.ToArray());

        // Packet two: the rest of the third, which does not fit, and a second one for the remainder.
        var rest = third.Skip(consumed).ToArray();
        var two = Packet(0x11, 1, start: false, rest.Take(184).ToArray());
        var three = Packet(0x11, 2, start: false, rest.Skip(184).ToArray());

        var found = Assemble([one, two, three]);

        Assert.Equal(3, found.Count);
        Assert.Equal(first, found[0]);
        Assert.Equal(second, found[1]);
        Assert.Equal(third, found[2]);
    }

    [Fact]
    public void The_pointer_field_finishes_the_previous_section_before_the_next_starts()
    {
        var first = TsBuilder.Section(0x42, 1, 0, [.. Enumerable.Repeat((byte)0x11, 250)]);
        var second = TsBuilder.Section(0x42, 1, 1, [0, 1, 0xFF]);

        var one = Packet(0x11, 0, start: true, [0, .. first.Take(183)]);
        var tail = first.Skip(183).ToArray();
        var two = Packet(0x11, 1, start: true, [(byte)tail.Length, .. tail, .. second]);

        var found = Assemble([one, two]);

        Assert.Equal(2, found.Count);
        Assert.Equal(first, found[0]);
        Assert.Equal(second, found[1]);
    }

    [Fact]
    public void A_damaged_section_is_dropped_rather_than_believed()
    {
        var section = TsBuilder.Pat(1, 0, (1, 0x100));
        section[8] ^= 0xFF;

        Assert.Empty(Assemble(TsBuilder.Packets(section, 0)));
    }

    [Fact]
    public void A_lost_packet_drops_the_section_it_was_part_of()
    {
        var streams = Enumerable.Range(0, 60).Select(index => (0x04, 0x100 + index, Array.Empty<byte>())).ToArray();
        var section = TsBuilder.Pmt(1, 0, 0x100, [], streams);
        var packets = TsBuilder.Packets(section, 0x50);
        packets.RemoveAt(1);

        Assert.Empty(Assemble(packets));
    }

    [Fact]
    public void A_packet_sent_twice_is_taken_once()
    {
        var section = TsBuilder.Pat(1, 0, (1, 0x100));
        var packet = TsBuilder.Packets(section, 0)[0];

        Assert.Single(Assemble([packet, packet]));
    }

    [Fact]
    public void Packets_cut_anywhere_are_put_back_in_step()
    {
        var packets = TsBuilder.Join(Enumerable.Range(0, 5).Select(index => TsBuilder.EsPacket(0x100, index, (byte)index)));
        var aligner = new PacketAligner();
        var output = new List<byte>();
        var buffer = new byte[packets.Length + TransportPacket.Size];

        // Garbage in front, then pieces of 100 bytes: nothing lines up with a packet boundary.
        var stream = new byte[] { 0x12, 0x47, 0x00 }.Concat(packets).ToArray();
        foreach (var piece in stream.Chunk(100))
        {
            var written = aligner.Push(piece, buffer);
            output.AddRange(buffer.Take(written));
        }

        Assert.Equal(packets, output.ToArray());
    }

    private static List<byte[]> Assemble(IEnumerable<byte[]> packets)
    {
        var assembler = new SectionAssembler();
        var found = new List<byte[]>();

        foreach (var packet in packets)
        {
            assembler.Push(packet, section => found.Add(section.ToArray()));
        }

        return found;
    }

    private static byte[] Packet(int pid, int continuity, bool start, byte[] payload)
    {
        var packet = new byte[TransportPacket.Size];
        TransportPacket.WriteHeader(packet, pid, start, continuity);
        packet.AsSpan(4).Fill(0xFF);
        payload.CopyTo(packet, 4);

        return packet;
    }
}
