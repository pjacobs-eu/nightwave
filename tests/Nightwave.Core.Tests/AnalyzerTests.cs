using Nightwave.Core.Analysis;

namespace Nightwave.Core.Tests;

public sealed class AnalyzerTests
{
    private const int PmtPid = 0x100;
    private const int VideoPid = 0x101;
    private const int AudioPid = 0x102;

    private static byte[] Packet(int pid, bool unitStart, int counter, ReadOnlySpan<byte> payload, byte[]? adaptation = null)
    {
        var packet = new byte[188];

        packet.AsSpan().Fill(0xFF);
        packet[0] = 0x47;
        packet[1] = (byte)((unitStart ? 0x40 : 0) | (pid >> 8));
        packet[2] = (byte)pid;

        var offset = 4;

        if (adaptation is not null)
        {
            packet[3] = (byte)(0x30 | counter);
            packet[4] = (byte)adaptation.Length;
            adaptation.CopyTo(packet, 5);
            offset = 5 + adaptation.Length;
        }
        else
        {
            packet[3] = (byte)(0x10 | counter);
        }

        payload.CopyTo(packet.AsSpan(offset));

        return packet;
    }

    private static byte[] Pat() => Packet(0, true, 0, [0, 0x00, 0xB0, 13, 0, 1, 0xC1, 0, 0, 0, 1, (byte)(0xE0 | (PmtPid >> 8)), unchecked((byte)PmtPid), 0, 0, 0, 0]);

    private static byte[] Pmt()
    {
        byte[] body =
        [
            0, 1, 0xC1, 0, 0,
            (byte)(0xE0 | (VideoPid >> 8)), unchecked((byte)VideoPid),
            0xF0, 6, 0x09, 4, 0x18, 0x30, 0xE0, 0x50,
            0x1B, (byte)(0xE0 | (VideoPid >> 8)), unchecked((byte)VideoPid), 0xF0, 0,
            0x03, (byte)(0xE0 | (AudioPid >> 8)), unchecked((byte)AudioPid), 0xF0, 6, 0x0A, 4, (byte)'d', (byte)'e', (byte)'u', 0,
        ];

        var section = new List<byte> { 0, 0x02, 0xB0, (byte)(body.Length + 4) };

        section.AddRange(body);
        section.AddRange(new byte[] { 0, 0, 0, 0 });

        return Packet(PmtPid, true, 0, [.. section]);
    }

    /// <summary>A High profile SPS of 1280x720 progressive at 50 frames per second, written bit by bit.</summary>
    private static byte[] Sps()
    {
        var bits = new BitWriter();

        bits.Write(100, 8);
        bits.Write(0, 8);
        bits.Write(32, 8);
        bits.Golomb(0);
        bits.Golomb(1);
        bits.Golomb(0);
        bits.Golomb(0);
        bits.Write(0, 1);
        bits.Write(0, 1);
        bits.Golomb(0);
        bits.Golomb(0);
        bits.Golomb(0);
        bits.Golomb(4);
        bits.Write(0, 1);
        bits.Golomb(79);
        bits.Golomb(44);
        bits.Write(1, 1);
        bits.Write(1, 1);
        bits.Write(0, 1);
        bits.Write(1, 1);
        bits.Write(0, 1);
        bits.Write(0, 1);
        bits.Write(0, 1);
        bits.Write(0, 1);
        bits.Write(1, 1);
        bits.Write(1, 32);
        bits.Write(100, 32);
        bits.Write(1, 1);

        return [0, 0, 0, 1, 0x67, .. bits.Bytes(), 0, 0, 1, 0x68, 0xEE];
    }

    private static byte[] Pes(ReadOnlySpan<byte> elementary) => [0, 0, 1, 0xE0, 0, 0, 0x80, 0, 0, .. elementary];

    private sealed class BitWriter
    {
        private readonly List<int> _bits = [];

        public void Write(long value, int count)
        {
            for (var bit = count - 1; bit >= 0; bit--)
            {
                _bits.Add((int)((value >> bit) & 1));
            }
        }

        public void Golomb(int value)
        {
            var code = value + 1;
            var length = 32 - int.LeadingZeroCount(code);

            Write(0, length - 1);
            Write(code, length);
        }

        public byte[] Bytes()
        {
            while (_bits.Count % 8 != 0)
            {
                _bits.Add(0);
            }

            // Two zero bytes in a row get an emulation prevention byte, as in any real stream.
            var bytes = new List<byte>();

            foreach (var value in _bits.Chunk(8).Select(chunk => (byte)chunk.Aggregate(0, (value, bit) => (value << 1) | bit)))
            {
                if (value <= 3 && bytes.Count >= 2 && bytes[^1] == 0 && bytes[^2] == 0)
                {
                    bytes.Add(3);
                }

                bytes.Add(value);
            }

            return [.. bytes];
        }
    }

    [Fact]
    public void The_tables_say_what_the_stream_carries()
    {
        var analyzer = new TsAnalyzer();

        analyzer.Inspect(Pat());
        analyzer.Inspect(Pmt());

        var result = analyzer.Snapshot();

        Assert.Equal(1, result.ProgramNumber);
        Assert.Equal(PmtPid, result.PmtPid);
        Assert.Equal(VideoPid, result.PcrPid);
        Assert.Equal([0x1830], result.CaSystems);
        Assert.Equal([new CaEntry(0x1830, 0x50)], result.CaEntries);
        Assert.Equal("H.264", result.Pids.Single(pid => pid.Pid == VideoPid).Codec);

        var audio = result.Pids.Single(pid => pid.Pid == AudioPid);

        Assert.Equal(("audio", "MPEG Audio", "deu"), (audio.Kind, audio.Codec, audio.Language));
    }

    [Fact]
    public void The_sequence_header_gives_size_rate_and_profile()
    {
        var format = VideoHeaders.Find(Sps(), "H.264");

        Assert.NotNull(format);
        Assert.Equal((1280, 720), (format.Width, format.Height));
        Assert.Equal(50, format.FrameRate);
        Assert.False(format.Interlaced);
        Assert.Equal(("High", "3.2", 8, "4:2:0"), (format.Profile, format.Level, format.BitDepth, format.ChromaFormat));
    }

    [Fact]
    public void A_video_pid_is_read_once_its_next_unit_starts()
    {
        var analyzer = new TsAnalyzer();

        analyzer.Inspect(Pat());
        analyzer.Inspect(Pmt());
        analyzer.Inspect(Packet(VideoPid, true, 0, Pes(Sps())));
        analyzer.Inspect(Packet(VideoPid, true, 1, Pes([0, 0, 1, 0x09])));

        Assert.Equal(720, analyzer.Snapshot().Video?.Height);
    }

    [Fact]
    public void Mpeg2_headers_say_whether_it_is_interlaced()
    {
        byte[] header = [0, 0, 1, 0xB3, 0x2D, 0x02, 0x40, 0x33, 0, 0, 0, 0, 0, 0, 1, 0xB5, 0x14, 0x80, 0, 0];

        var format = VideoHeaders.Find(header, "MPEG-2");

        Assert.NotNull(format);
        Assert.Equal((720, 576, 25d, "16:9", true), (format.Width, format.Height, format.FrameRate, format.AspectRatio, format.Interlaced));
    }

    [Fact]
    public void Audio_frame_headers_are_read()
    {
        Assert.Equal((48000, 192, "2.0"), Fields(AudioHeaders.Find([0xFF, 0xFD, 0xA4, 0x00, 0, 0, 0, 0, 0], "MPEG Audio")));

        // AC-3, 48 kHz, 448 kbit/s, 3/2 with LFE.
        Assert.Equal((48000, 448, "5.1"), Fields(AudioHeaders.Find([0x0B, 0x77, 0, 0, 0x1E, 0x40, 0xE1, 0x80, 0, 0], "AC-3")));
        Assert.Equal((48000, null, "2.0"), Fields(AudioHeaders.Find([0xFF, 0xF1, 0x4C, 0x80, 0, 0, 0, 0, 0], "AAC")));

        static (int?, int?, string?) Fields(AudioFormat? format) => (format?.SampleRate, format?.BitRate, format?.Channels);
    }

    [Fact]
    public void Errors_are_counted_the_way_tr_101_290_names_them()
    {
        var analyzer = new TsAnalyzer();
        var damaged = Packet(AudioPid, false, 3, []);

        damaged[1] |= 0x80;

        analyzer.Inspect(Packet(AudioPid, false, 0, []));
        analyzer.Inspect(Packet(AudioPid, false, 1, []));
        analyzer.Inspect(Packet(AudioPid, false, 5, []));
        analyzer.Inspect(damaged);
        analyzer.Inspect([1, 2, 3]);

        var result = analyzer.Snapshot();

        Assert.Equal(1, result.ContinuityErrors);
        Assert.Equal(1, result.TransportErrors);
        Assert.Equal(3, result.SyncLossBytes);
    }

    [Fact]
    public void Pcr_intervals_are_measured()
    {
        var analyzer = new TsAnalyzer();

        analyzer.Inspect(Pat());
        analyzer.Inspect(Pmt());

        byte[] Pcr(long ms)
        {
            var @base = ms * 90;

            return [0x10, (byte)(@base >> 25), (byte)(@base >> 17), (byte)(@base >> 9), (byte)(@base >> 1), (byte)(((@base & 1) << 7) | 0x7E), 0];
        }

        analyzer.Inspect(Packet(VideoPid, false, 0, [], Pcr(0)));
        analyzer.Inspect(Packet(VideoPid, false, 1, [], Pcr(40)));
        analyzer.Inspect(Packet(VideoPid, false, 2, [], Pcr(200)));

        var result = analyzer.Snapshot();

        Assert.Equal(160, result.PcrIntervalMaximumMs!.Value, 0);
        Assert.Equal(100, result.PcrIntervalAverageMs!.Value, 0);
        Assert.Equal(1, result.PcrIntervalErrors);
    }

    [Fact]
    public void Scrambled_packets_are_seen_as_such_and_not_read()
    {
        var analyzer = new TsAnalyzer();
        var packet = Packet(0, true, 0, [1, 2, 3]);

        packet[3] |= 0x80;
        analyzer.Inspect(packet);

        Assert.True(analyzer.Snapshot().Pids.Single().Scrambled);
    }

    [Fact]
    public void Garbage_never_throws()
    {
        var analyzer = new TsAnalyzer();
        var random = new Random(1);
        var data = new byte[188 * 2000];

        random.NextBytes(data);

        for (var index = 0; index < data.Length; index += 188)
        {
            data[index] = 0x47;
        }

        analyzer.Inspect(data);
        analyzer.Inspect(Pat());
        analyzer.Inspect(Pmt());
        analyzer.Inspect(data);

        Assert.True(analyzer.Snapshot().Packets > 0);
    }

    [Fact]
    public void What_a_carrier_can_carry_comes_from_the_norm()
    {
        Assert.Equal(38.01, CarrierCapacity.UsefulBitRate("DVB-S", 27500, "qpsk", "34")!.Value, 2);
        Assert.Equal(43.57, CarrierCapacity.UsefulBitRate("DVB-S2", 22000, "8psk", "23")!.Value, 2);
        Assert.Equal(6.62, CarrierCapacity.RequiredEsN0("DVB-S2", "8PSK", "23"));
        Assert.Null(CarrierCapacity.RequiredEsN0("DVB-S2", "qpsk", "78"));

        // The table is DVB-S2's; a DVB-S carrier with the same code rate is not covered by it.
        Assert.Null(CarrierCapacity.RequiredEsN0("DVB-S", "qpsk", "56"));
        Assert.Equal("9/10", CarrierCapacity.FecName("910"));
    }
}
