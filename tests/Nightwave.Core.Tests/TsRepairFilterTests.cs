using Nightwave.Core.Streaming;

namespace Nightwave.Core.Tests;

public sealed class TsRepairFilterTests
{
    private const int Video = 0x100;
    private const int Audio = 0x101;

    private static byte[] Packet(int pid, int counter, bool unitStart = false, bool randomAccess = false, bool error = false, bool payload = true, byte fill = 0xAA)
    {
        var packet = new byte[TsRepairFilter.PacketSize];

        packet.AsSpan().Fill(fill);
        packet[0] = 0x47;
        packet[1] = (byte)((error ? 0x80 : 0) | (unitStart ? 0x40 : 0) | ((pid >> 8) & 0x1F));
        packet[2] = (byte)pid;
        packet[3] = (byte)((randomAccess ? 0x20 : 0) | (payload ? 0x10 : 0) | (counter & 0x0F));

        if (randomAccess)
        {
            packet[4] = 1;
            packet[5] = 0x40;
        }

        return packet;
    }

    private static List<byte[]> Run(TsRepairFilter filter, params byte[][] packets)
    {
        var input = packets.SelectMany(packet => packet).ToArray();
        var output = new byte[input.Length + TsRepairFilter.PacketSize];
        var written = filter.Process(input, output);

        return [.. output[..written].Chunk(TsRepairFilter.PacketSize)];
    }

    private static int Counter(byte[] packet) => packet[3] & 0x0F;

    [Fact]
    public void A_clean_stream_passes_byte_for_byte()
    {
        var filter = new TsRepairFilter();
        var packets = Enumerable.Range(0, 40).Select(index => Packet(Video, index, unitStart: index == 0, fill: (byte)index)).ToArray();

        var output = Run(filter, packets);

        Assert.Equal(packets.SelectMany(packet => packet), output.SelectMany(packet => packet));
        Assert.Equal(40, filter.Stats.PacketsOut);
    }

    [Fact]
    public void Packets_the_tuner_marked_as_damaged_are_dropped()
    {
        var filter = new TsRepairFilter();

        var output = Run(filter, Packet(Audio, 0), Packet(Audio, 1, error: true), Packet(Audio, 1));

        Assert.Equal(2, output.Count);
        Assert.Equal(1, filter.Stats.ErrorPacketsDropped);
    }

    [Fact]
    public void A_repeated_packet_is_dropped()
    {
        var filter = new TsRepairFilter();

        var output = Run(filter, Packet(Audio, 3), Packet(Audio, 3), Packet(Audio, 4));

        Assert.Equal(2, output.Count);
        Assert.Equal(1, filter.Stats.DuplicatesDropped);
    }

    [Fact]
    public void After_a_gap_a_pid_resumes_at_the_next_unit_start()
    {
        var filter = new TsRepairFilter();

        var output = Run(
            filter,
            Packet(Audio, 0, unitStart: true),
            Packet(Audio, 1),
            Packet(Audio, 5),
            Packet(Audio, 6),
            Packet(Audio, 7, unitStart: true),
            Packet(Audio, 8));

        Assert.Equal(4, output.Count);
        Assert.Equal(1, filter.Stats.GapsFound);
        Assert.Equal(2, filter.Stats.PacketsHeldBack);
    }

    [Fact]
    public void Video_with_random_access_points_waits_for_the_next_one_rather_than_any_unit()
    {
        var filter = new TsRepairFilter();

        var output = Run(
            filter,
            Packet(Video, 0, unitStart: true, randomAccess: true),
            Packet(Video, 5, unitStart: true),
            Packet(Video, 6, unitStart: true),
            Packet(Video, 7, unitStart: true, randomAccess: true));

        Assert.Equal(2, output.Count);
        Assert.Equal(2, filter.Stats.PacketsHeldBack);
    }

    [Fact]
    public void What_goes_out_after_a_gap_continues_the_counter()
    {
        var filter = new TsRepairFilter();

        var output = Run(filter, Packet(Audio, 2), Packet(Audio, 3), Packet(Audio, 9, unitStart: true), Packet(Audio, 10));

        Assert.Equal([2, 3, 4, 5], output.Select(Counter));
    }

    [Fact]
    public void A_discontinuity_the_broadcaster_flags_is_no_gap()
    {
        var filter = new TsRepairFilter();
        var flagged = Packet(Audio, 9);

        flagged[3] |= 0x20;
        flagged[4] = 1;
        flagged[5] = 0x80;

        var output = Run(filter, Packet(Audio, 2), flagged, Packet(Audio, 10));

        Assert.Equal(3, output.Count);
        Assert.Equal(0, filter.Stats.GapsFound);
    }

    [Fact]
    public void A_packet_without_payload_does_not_count()
    {
        var filter = new TsRepairFilter();

        var output = Run(filter, Packet(Audio, 2), Packet(Audio, 2, payload: false), Packet(Audio, 3));

        Assert.Equal(3, output.Count);
        Assert.Equal(0, filter.Stats.DuplicatesDropped);
    }

    [Fact]
    public void Packets_split_across_reads_come_out_whole()
    {
        var filter = new TsRepairFilter();
        var packets = Enumerable.Range(0, 6).Select(index => Packet(Audio, index, fill: (byte)(index + 1))).ToArray();
        var input = packets.SelectMany(packet => packet).ToArray();
        var output = new byte[input.Length + TsRepairFilter.PacketSize];
        var written = 0;

        foreach (var part in input.Chunk(100))
        {
            written += filter.Process(part, output.AsSpan(written));
        }

        Assert.Equal(input[..(188 * 5)], output[..(188 * 5)]);
        Assert.True(written >= 188 * 5);
        Assert.Equal(0, filter.Stats.BytesResynchronised);
    }

    [Fact]
    public void Garbage_between_packets_is_skipped_and_the_stream_found_again()
    {
        var filter = new TsRepairFilter();
        var input = Packet(Audio, 0).Concat(new byte[] { 1, 2, 3, 4, 5 }).Concat(Packet(Audio, 1)).Concat(Packet(Audio, 2)).ToArray();
        var output = new byte[input.Length + TsRepairFilter.PacketSize];

        var written = filter.Process(input, output);

        // The packet in front of the garbage has no sync byte behind it, which is what a false
        // sync looks like too, so it goes with the garbage. The next two are whole.
        Assert.Equal(2 * TsRepairFilter.PacketSize, written);
        Assert.Equal(TsRepairFilter.PacketSize + 5, filter.Stats.BytesResynchronised);
        Assert.Equal(0, filter.Stats.GapsFound);
    }

    [Fact]
    public void Null_packets_pass_untouched()
    {
        var filter = new TsRepairFilter();

        var output = Run(filter, Packet(0x1FFF, 0), Packet(0x1FFF, 0), Packet(0x1FFF, 0));

        Assert.Equal(3, output.Count);
    }

    private static byte[] Picture(int counter, long pts, bool randomAccess)
    {
        var packet = Packet(Video, counter, unitStart: true, randomAccess: randomAccess);
        var offset = randomAccess ? 6 : 4;

        // The adaptation field of the helper is two bytes long.
        packet[offset] = 0;
        packet[offset + 1] = 0;
        packet[offset + 2] = 1;
        packet[offset + 3] = 0xE0;
        packet[offset + 7] = 0x80;
        packet[offset + 8] = 5;
        packet[offset + 9] = (byte)(0x21 | ((pts >> 29) & 0x0E));
        packet[offset + 10] = (byte)(pts >> 22);
        packet[offset + 11] = (byte)(((pts >> 14) & 0xFE) | 1);
        packet[offset + 12] = (byte)(pts >> 7);
        packet[offset + 13] = (byte)(((pts << 1) & 0xFE) | 1);

        return packet;
    }

    [Fact]
    public void Pictures_shown_before_the_keyframe_of_a_resume_are_left_out()
    {
        var filter = new TsRepairFilter();

        var output = Run(
            filter,
            Picture(0, 1000, true),
            Picture(1, 2000, false),
            Picture(8, 3000, false),
            Picture(9, 9000, true),
            Picture(10, 8000, false),
            Packet(Video, 11),
            Picture(12, 8500, false),
            Picture(13, 10000, false),
            Picture(14, 9500, false));

        // 1000, 2000, the keyframe, and the two behind it; 8000 and 8500 came before it.
        Assert.Equal(5, output.Count);
        Assert.Equal(3, filter.Stats.LeadingPacketsDropped);
    }

    [Fact]
    public void Joining_leaves_out_what_refers_to_before_the_first_keyframe_too()
    {
        var filter = new TsRepairFilter();

        var output = Run(filter, Picture(0, 5000, true), Picture(1, 4000, false), Picture(2, 7000, false));

        Assert.Equal(2, output.Count);
    }
}
