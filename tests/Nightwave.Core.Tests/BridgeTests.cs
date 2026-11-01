using Nightwave.Core.Streaming;

namespace Nightwave.Core.Tests;

public sealed class BridgeTests
{
    private const int Video = 0x100;

    private static byte[] Pes(int pid, int counter, long pts, bool randomAccess, byte stream = 0xE0, long pcr = -1)
    {
        var packet = new byte[188];

        packet.AsSpan().Fill(0xAA);
        packet[0] = 0x47;
        packet[1] = (byte)(0x40 | (pid >> 8));
        packet[2] = unchecked((byte)pid);
        packet[3] = (byte)(0x30 | counter);
        packet[4] = 7;
        packet[5] = (byte)((randomAccess ? 0x40 : 0) | (pcr >= 0 ? 0x10 : 0));

        if (pcr >= 0)
        {
            var @base = pcr / 300;

            packet[6] = (byte)(@base >> 25);
            packet[7] = (byte)(@base >> 17);
            packet[8] = (byte)(@base >> 9);
            packet[9] = (byte)(@base >> 1);
            packet[10] = (byte)(((@base & 1) << 7) | 0x7E);
            packet[11] = 0;
        }

        var offset = 12;

        packet[offset] = 0;
        packet[offset + 1] = 0;
        packet[offset + 2] = 1;
        packet[offset + 3] = stream;
        packet[offset + 7] = 0x80;
        packet[offset + 8] = 5;
        WritePts(packet.AsSpan(offset + 9), pts);

        return packet;
    }

    private static void WritePts(Span<byte> field, long pts)
    {
        field[0] = (byte)(0x21 | ((pts >> 29) & 0x0E));
        field[1] = (byte)(pts >> 22);
        field[2] = (byte)(((pts >> 14) & 0xFE) | 1);
        field[3] = (byte)(pts >> 7);
        field[4] = (byte)(((pts << 1) & 0xFE) | 1);
    }

    private static long ReadPts(ReadOnlySpan<byte> field) =>
        ((long)((field[0] >> 1) & 7) << 30) | ((long)field[1] << 22) | ((long)(field[2] >> 1) << 15) | ((long)field[3] << 7) | ((long)field[4] >> 1);

    private static StreamBridge Primed()
    {
        var bridge = new StreamBridge(_ => null);

        bridge.Pass(Pes(Video, 0, 90_000, true, pcr: 27_000_000), 1000);
        bridge.Pass(Pes(Video, 1, 180_000, false), 2000);

        return bridge;
    }

    [Fact]
    public void Nothing_happens_while_pictures_keep_coming()
    {
        var bridge = Primed();

        Assert.False(bridge.ShouldFill(2000 + StreamBridge.PatienceMs - 1));
    }

    [Fact]
    public void An_outage_repeats_the_keyframe_with_the_clock_running_on()
    {
        var bridge = Primed();
        var now = 2000 + StreamBridge.PatienceMs;

        Assert.True(bridge.ShouldFill(now));

        var first = new List<byte>();

        bridge.Fill(first, now);

        var later = new List<byte>();

        bridge.Fill(later, now + 1000);

        Assert.True(bridge.Stats.Active);
        Assert.Equal(2, bridge.Stats.PicturesInserted);

        long TimeOf(List<byte> output)
        {
            var bytes = output.ToArray();

            for (var at = 0; at < bytes.Length; at += 188)
            {
                if (((bytes[at + 1] & 0x1F) << 8 | bytes[at + 2]) == Video && (bytes[at + 1] & 0x40) != 0)
                {
                    return ReadPts(bytes.AsSpan(at + 12 + 9));
                }
            }

            return -1;
        }

        Assert.Equal(90_000, TimeOf(later) - TimeOf(first));
    }

    [Fact]
    public void Counters_stay_continuous_across_the_outage()
    {
        var bridge = Primed();
        var fill = new List<byte>();

        bridge.Fill(fill, 3000);

        var bytes = fill.ToArray();
        var last = 0;

        for (var at = 0; at < bytes.Length; at += 188)
        {
            if (((bytes[at + 1] & 0x1F) << 8 | bytes[at + 2]) == Video && (bytes[at + 3] & 0x10) != 0)
            {
                last = bytes[at + 3] & 0x0F;
            }
        }

        var resumed = Pes(Video, 9, 900_000, true);

        bridge.Pass(resumed, 3500);

        Assert.Equal((last + 1) & 0x0F, resumed[3] & 0x0F);
        Assert.False(bridge.Stats.Active);
    }

    [Fact]
    public void It_stops_after_a_while_rather_than_pretending_for_ever()
    {
        var bridge = Primed();
        var fill = new List<byte>();

        bridge.Fill(fill, 3000);

        Assert.False(bridge.ShouldFill(3000 + StreamBridge.MaximumFreezeMs));
    }

    [Fact]
    public void Without_a_keyframe_there_is_nothing_to_show()
    {
        var bridge = new StreamBridge(_ => null);

        bridge.Pass(Pes(Video, 0, 90_000, false), 0);

        Assert.False(bridge.ShouldFill(10_000));
    }

    [Fact]
    public void Silent_frames_have_the_format_of_the_stream()
    {
        var mpeg = SilentAudio.MpegFrame([0xFF, 0xFD, 0xA4, 0x00]);

        Assert.Equal(576, mpeg!.Length);
        Assert.All(mpeg[4..], value => Assert.Equal(0, value));

        // 448 kbit/s, 48 kHz, 5.1.
        var ac3 = SilentAudio.Ac3Frame([0x0B, 0x77, 0, 0, 0x1E, 0x40, 0xE1, 0x80]);

        Assert.Equal(1792, ac3!.Length);
        Assert.Equal((0x0B, 0x77, 0x1E), (ac3[0], ac3[1], ac3[4]));

        // 32 kbit/s cannot hold the frame.
        Assert.Null(SilentAudio.Ac3Frame([0x0B, 0x77, 0, 0, 0x00, 0x40, 0x41, 0x80]));
    }

    [Fact]
    public void Video_packets_without_a_timestamp_count_as_a_living_picture()
    {
        var bridge = Primed();
        var continuation = new byte[188];

        continuation.AsSpan().Fill(0xAA);
        continuation[0] = 0x47;
        continuation[1] = 1;
        continuation[2] = 0;
        continuation[3] = 0x12;

        bridge.Pass(continuation, 2400);

        Assert.False(bridge.ShouldFill(2400 + StreamBridge.PatienceMs - 1));
    }

    [Fact]
    public async Task A_reader_that_stalls_is_no_outage()
    {
        var packets = new List<byte[]> { Pes(Video, 0, 90_000, true, pcr: 27_000_000), Pes(Video, 1, 91_800, false), Pes(Video, 2, 93_600, false) };

        using var source = new TickingStream(packets, TimeSpan.FromMilliseconds(40));

        var bridge = new StreamBridge(_ => null);

        await using var stream = new BridgingStream(source, bridge);

        var buffer = new byte[188 * 513];
        var started = DateTime.UtcNow;
        var reads = 0;

        while (DateTime.UtcNow - started < TimeSpan.FromSeconds(2))
        {
            Assert.True(await stream.ReadAsync(buffer) > 0);

            // The player falls behind now and then, while the source keeps delivering.
            if (++reads == 4)
            {
                await Task.Delay(900);
            }
        }

        Assert.Equal(0, bridge.Stats.Outages);
    }

    /// <summary>Delivers a packet every few milliseconds, its picture running on for ever.</summary>
    private sealed class TickingStream(List<byte[]> first, TimeSpan every) : Stream
    {
        private int _next;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(every, cancellationToken);

            var packet = _next < first.Count ? first[_next] : Pes(Video, _next & 0x0F, 90_000 + (_next * 3600L), _next % 12 == 0);

            _next++;
            packet.CopyTo(buffer);

            return packet.Length;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
