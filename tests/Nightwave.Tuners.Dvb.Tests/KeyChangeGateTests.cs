using Nightwave.Tuners.Dvb.Descrambling;
using Nightwave.Tuners.Dvb.Streaming;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>
/// A key that comes after the broadcaster changed the parity: the packets wait for it, and what
/// could not wait is left scrambled rather than descrambled with the key before.
/// </summary>
public sealed class KeyChangeGateTests : IAsyncDisposable
{
    private const int Video = 0x101;
    private const int Audio = 0x102;
    private const int Even = 2;
    private const int Odd = 3;

    private static readonly HashSet<int> Pids = [Video, Audio];

    private readonly ManualClock _clock = new();
    private readonly FakeDescrambler _descrambler = new(new ServiceDescramblingRequest
    {
        ServiceId = 1,
        ProgramMapSection = new byte[1],
        ProgramMapPid = 0x100,
        Slot = 0,
        TransportStreamId = 1,
        OriginalNetworkId = 1,
        ChannelName = "Test channel",
    });

    private readonly List<(int Mark, bool Descramble)> _released = [];
    private readonly List<long> _late = [];
    private readonly List<long> _expired = [];
    private readonly KeyChangeGate _gate;
    private int _mark;

    public KeyChangeGateTests() =>
        _gate = new KeyChangeGate(_clock, (_, count) => _late.Add(count), _expired.Add);

    public ValueTask DisposeAsync() => _descrambler.DisposeAsync();

    private byte[] Packet(int pid, int parity, bool unitStart = false)
    {
        var packet = new byte[188];

        packet[0] = 0x47;
        packet[1] = (byte)((unitStart ? 0x40 : 0) | (pid >> 8));
        packet[2] = (byte)pid;
        packet[3] = (byte)((parity << 6) | 0x10);
        packet[4] = (byte)++_mark;

        return packet;
    }

    private void Push(params byte[][] packets)
    {
        var joined = packets.SelectMany(packet => packet).ToArray();

        _gate.Push(joined, _descrambler, Pids, Take);
    }

    private void Take(Span<byte> packets, bool descramble)
    {
        for (var offset = 0; offset + 188 <= packets.Length; offset += 188)
        {
            _released.Add((packets[offset + 4], descramble));
        }
    }

    private int[] Marks => [.. _released.Select(entry => entry.Mark)];

    /// <summary>Starts on the even key, confirmed.</summary>
    private void Running()
    {
        Push(Packet(Video, Even, unitStart: true), Packet(Audio, Even, unitStart: true));
        _released.Clear();
    }

    [Fact]
    public void A_key_that_is_there_in_time_holds_nothing_back()
    {
        Running();

        Push(Packet(Video, Even), Packet(Video, Odd, unitStart: true), Packet(Video, Odd));

        Assert.Equal([3, 4, 5], Marks);
        Assert.All(_released, entry => Assert.True(entry.Descramble));
        Assert.Empty(_late);
    }

    [Fact]
    public void The_packets_before_a_late_key_wait_for_it_and_lose_nothing()
    {
        Running();
        _descrambler.ProbeResult = KeyProbe.Wrong;

        Push(Packet(Video, Even), Packet(Video, Odd, unitStart: true), Packet(Audio, Odd));
        Push(Packet(Video, Odd));

        // What came before the change goes on; from the change on, nothing yet.
        Assert.Equal([3], Marks);

        _clock.Advance(TimeSpan.FromMilliseconds(220));
        _descrambler.ProbeResult = KeyProbe.Fits;
        Push(Packet(Audio, Odd));

        Assert.Equal([3, 4, 5, 6, 7], Marks);
        Assert.All(_released, entry => Assert.True(entry.Descramble));
        Assert.Equal([4L], _late);
    }

    [Fact]
    public void A_wait_ends_at_the_next_unit_start_that_fits_within_a_batch()
    {
        Running();
        _descrambler.ProbeResult = KeyProbe.Wrong;
        Push(Packet(Video, Odd, unitStart: true));

        _descrambler.ProbeResult = KeyProbe.Fits;
        Push(Packet(Audio, Odd), Packet(Video, Odd, unitStart: true), Packet(Video, Odd));

        Assert.Equal([3, 4, 5, 6], Marks);
        Assert.Empty(_expired);
    }

    [Fact]
    public void A_key_that_stays_away_leaves_the_packets_scrambled_until_one_fits()
    {
        Running();
        _descrambler.ProbeResult = KeyProbe.Wrong;
        Push(Packet(Video, Odd, unitStart: true));

        _clock.Advance(KeyChangeGate.Patience);
        Push(Packet(Video, Odd));

        Assert.Equal([(3, false), (4, false)], _released);
        Assert.Equal([2L], _expired);

        Push(Packet(Video, Odd, unitStart: true), Packet(Video, Odd));
        Assert.All(_released, entry => Assert.False(entry.Descramble));

        _released.Clear();
        _descrambler.ProbeResult = KeyProbe.Fits;
        Push(Packet(Video, Odd), Packet(Video, Odd, unitStart: true), Packet(Video, Odd));

        Assert.Equal([(7, false), (8, true), (9, true)], _released);
    }

    /// <summary>
    /// Measured on a live join: the first key was the one for the next period, the period changed
    /// 200 ms later, and a wait still set on the old parity left the picture out for seven seconds.
    /// </summary>
    [Fact]
    public void A_change_during_the_wait_is_waited_for_instead()
    {
        _descrambler.ProbeResult = KeyProbe.Wrong;
        Push(Packet(Video, Odd, unitStart: true));
        Push(Packet(Video, Even, unitStart: true));

        _descrambler.ProbeResult = KeyProbe.Fits;
        Push(Packet(Video, Even, unitStart: true));

        Assert.Equal([1, 2, 3], Marks);
        Assert.All(_released, entry => Assert.True(entry.Descramble));
    }

    [Fact]
    public void A_change_after_the_wait_ran_out_is_tried_at_once()
    {
        _descrambler.ProbeResult = KeyProbe.Wrong;
        Push(Packet(Video, Odd, unitStart: true));
        Push(Packet(Video, Even, unitStart: true));
        _clock.Advance(KeyChangeGate.Patience);
        Push(Packet(Video, Even));
        _released.Clear();

        _descrambler.ProbeResult = KeyProbe.Fits;
        Push(Packet(Video, Even, unitStart: true));

        Assert.Equal([(4, true)], _released);
    }

    [Fact]
    public void The_stream_that_changes_last_does_not_start_another_wait()
    {
        Running();
        Push(Packet(Video, Odd, unitStart: true));

        _descrambler.ProbeResult = KeyProbe.Wrong;
        var probes = _descrambler.Probes;
        Push(Packet(Audio, Even), Packet(Audio, Odd, unitStart: true), Packet(Audio, Odd));

        Assert.Equal([3, 4, 5, 6], Marks);
        Assert.Equal(probes, _descrambler.Probes);
    }

    [Fact]
    public void Only_a_change_on_a_stream_with_a_start_code_counts()
    {
        Running();
        _descrambler.ProbeResult = KeyProbe.Wrong;

        Push(Packet(0x103, Odd, unitStart: true), Packet(Video, Even));

        Assert.Equal([3, 4], Marks);
    }

    [Fact]
    public void A_key_that_never_changes_is_tried_and_goes_straight_on()
    {
        Running();

        for (var change = 0; change < 4; change++)
        {
            Push(Packet(Video, change % 2 == 0 ? Odd : Even, unitStart: true));
        }

        Assert.Equal(4, _released.Count);
        Assert.Empty(_late);
        Assert.Empty(_expired);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
