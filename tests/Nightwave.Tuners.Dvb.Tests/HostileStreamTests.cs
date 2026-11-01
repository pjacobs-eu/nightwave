using Nightwave.Tuners.Dvb.Descrambling;
using Nightwave.Tuners.Dvb.Mpeg;
using Nightwave.Tuners.Dvb.Mpeg.Psi;
using Nightwave.Tuners.Dvb.Mpeg.Si;
using Nightwave.Tuners.Dvb.Transport;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>
/// What comes off a dish is not a file somebody wrote for this parser. A weak signal flips
/// bits, a multiplexer is free to send a length field that points past the section, and a
/// broadcaster may put anything at all in a descriptor. None of it may reach the server as an
/// exception: the reading loop of a tuner has no catch for one, and the tuner would take the
/// channels of everybody sharing it down with it.
/// </summary>
/// <remarks>
/// The seeds are fixed, so a failure names the stream that caused it and can be replayed.
/// </remarks>
public sealed class HostileStreamTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1019)]
    public void Random_bytes_are_cut_into_packets_and_read_without_throwing(int seed)
    {
        var random = new Random(seed);
        var aligner = new PacketAligner();
        var assemblers = new Dictionary<int, SectionAssembler>();
        var demux = new DemuxSectionAssembler();
        var output = new byte[(64 * 1024) + TransportPacket.Size];

        for (var chunk = 0; chunk < 200; chunk++)
        {
            var input = new byte[random.Next(1, 4096)];
            random.NextBytes(input);

            var count = aligner.Push(input, output);
            Assert.Equal(0, count % TransportPacket.Size);

            ReadEverything(output.AsSpan(0, count), assemblers, demux);
        }
    }

    /// <summary>
    /// The same, but starting from a real transponder: every packet is a valid one with a few
    /// bits flipped, which is what a section assembler actually meets - lengths and pointer
    /// fields that are almost right.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(11)]
    [InlineData(28006)]
    public void A_transponder_with_flipped_bits_is_read_without_throwing(int seed)
    {
        var random = new Random(seed);
        var assemblers = new Dictionary<int, SectionAssembler>();
        var demux = new DemuxSectionAssembler();
        var packets = SampleMux.Packets();

        Assert.NotEmpty(packets);

        foreach (var original in packets)
        {
            for (var pass = 0; pass < 8; pass++)
            {
                var packet = (byte[])original.Clone();

                for (var flips = random.Next(1, 6); flips > 0; flips--)
                {
                    packet[random.Next(packet.Length)] ^= (byte)(1 << random.Next(8));
                }

                ReadEverything(packet, assemblers, demux);
            }
        }
    }

    /// <summary>
    /// A section is handed to every parser that could claim it, whatever its table id says: a
    /// parser may refuse a section, but it may not throw at one.
    /// </summary>
    [Theory]
    [InlineData(3)]
    [InlineData(13)]
    public void Any_run_of_bytes_can_be_offered_to_every_table_parser(int seed)
    {
        var random = new Random(seed);

        // The table ids that make a parser read the section rather than refuse it at the first
        // byte: PAT, CAT, PMT, SDT actual and other, NIT actual and other, and the EIT range.
        int[] tableIds = [0x00, 0x01, 0x02, 0x40, 0x41, 0x42, 0x46, 0x4E, 0x4F, 0x50, 0x5F, 0x60, 0x6F];

        for (var attempt = 0; attempt < 20000; attempt++)
        {
            var section = new byte[random.Next(0, 400)];
            random.NextBytes(section);

            // Most of them are made to look like a section of a table this plugin reads: the
            // right table id, the long form, and a length that agrees with the size. Without
            // that the parsers refuse almost everything at their first check and the loops that
            // read descriptors are never reached.
            if (section.Length >= SectionHeader.Overhead && attempt % 4 != 0)
            {
                var length = section.Length - 3;

                section[0] = (byte)tableIds[random.Next(tableIds.Length)];
                section[1] = (byte)(0x80 | ((length >> 8) & 0x0F));
                section[2] = (byte)length;
                section[5] |= 0x01;
            }

            ParseEverything(section);
        }
    }

    [Theory]
    [InlineData(5)]
    [InlineData(17)]
    public void Any_run_of_bytes_is_a_text_field_that_decodes(int seed)
    {
        var random = new Random(seed);

        for (var attempt = 0; attempt < 3000; attempt++)
        {
            var text = new byte[random.Next(0, 64)];
            random.NextBytes(text);

            // The selector decides which table is used; every one of them has to be survivable.
            if (text.Length > 0 && attempt % 3 == 0)
            {
                text[0] = (byte)random.Next(0x00, 0x21);
            }

            Assert.NotNull(DvbText.Decode(text));
        }
    }

    [Theory]
    [InlineData(23)]
    [InlineData(29)]
    public void Any_datagram_is_an_rtp_packet_that_is_either_read_or_refused(int seed)
    {
        var random = new Random(seed);

        for (var attempt = 0; attempt < 5000; attempt++)
        {
            var datagram = new byte[random.Next(0, 80)];
            random.NextBytes(datagram);

            if (datagram.Length > 0 && attempt % 2 == 0)
            {
                // Version 2, so the header past the fixed part is read as well.
                datagram[0] = (byte)(0x80 | (datagram[0] & 0x3F));
            }

            if (RtpPacket.TryGetPayload(datagram, out var payload, out _))
            {
                var (offset, length) = payload.GetOffsetAndLength(datagram.Length);

                Assert.InRange(offset, 0, datagram.Length);
                Assert.InRange(length, 0, datagram.Length - offset);
            }
        }
    }

    /// <summary>Both section assemblers over the same packets, and every section they hand out parsed.</summary>
    private static void ReadEverything(
        ReadOnlySpan<byte> packets,
        Dictionary<int, SectionAssembler> assemblers,
        DemuxSectionAssembler demux)
    {
        for (var offset = 0; offset + TransportPacket.Size <= packets.Length; offset += TransportPacket.Size)
        {
            var packet = packets.Slice(offset, TransportPacket.Size);

            if (packet[0] != TransportPacket.SyncByte)
            {
                continue;
            }

            var pid = TransportPacket.Pid(packet);

            if (!assemblers.TryGetValue(pid, out var assembler))
            {
                assembler = new SectionAssembler();
                assemblers[pid] = assembler;
            }

            assembler.Push(packet, static section => ParseEverything(section));
            demux.Feed(packet, 0, static (section, _) => ParseEverything(section));
        }
    }

    private static void ParseEverything(ReadOnlySpan<byte> section)
    {
        ProgramAssociationTable.Parse(section);
        ProgramMapTable.Parse(section);
        ConditionalAccessTable.Parse(section);
        ServiceDescriptionTable.Parse(section);
        NetworkInformationTable.Parse(section);
        EventInformationTable.Parse(section);

        // Whatever the length field says, the body a parser reads descriptors out of is the one
        // the section actually has.
        if (section.Length > SectionHeader.Overhead)
        {
            Descriptor.ReadLoop(section[8..^4]);
        }
    }
}
