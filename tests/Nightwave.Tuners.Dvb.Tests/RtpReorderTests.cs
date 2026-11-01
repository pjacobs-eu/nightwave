using Nightwave.Tuners.Dvb.Transport;

namespace Nightwave.Tuners.Dvb.Tests;

public sealed class RtpReorderTests
{
    private readonly RtpReorderBuffer _buffer = new();
    private readonly List<int> _delivered = [];

    private void Take(params int[] sequences)
    {
        foreach (var sequence in sequences)
        {
            _buffer.Take(sequence, [(byte)sequence], (delivered, payload) =>
            {
                Assert.Equal((byte)delivered, payload[0]);
                _delivered.Add(delivered);
            });
        }
    }

    /// <summary>A stream of a few tables: a datagram every two seconds, and a gap must not hold it for a minute.</summary>
    [Fact]
    public void A_gap_on_a_sparse_stream_is_not_waited_for()
    {
        var now = 0L;
        var buffer = new RtpReorderBuffer(() => now);
        var delivered = new List<int>();

        void Arrive(int sequence)
        {
            now += 2000;
            buffer.Take(sequence, [0], (number, _) => delivered.Add(number));
        }

        Arrive(100);
        Arrive(105);
        Arrive(106);

        Assert.Equal([100, 105, 106], delivered);
    }

    [Fact]
    public void Numbers_starting_over_lower_after_a_quiet_moment_are_a_new_stream_not_latecomers()
    {
        var now = 0L;
        var buffer = new RtpReorderBuffer(() => now);
        var delivered = new List<int>();

        void Arrive(int sequence)
        {
            now += 2000;
            buffer.Take(sequence, [0], (number, _) => delivered.Add(number));
        }

        Arrive(500);
        Arrive(40);
        Arrive(41);

        Assert.Equal([500, 40, 41], delivered);
    }

    [Fact]
    public void Datagrams_close_together_are_still_sorted()
    {
        var now = 1000L;
        var buffer = new RtpReorderBuffer(() => now);
        var delivered = new List<int>();

        foreach (var sequence in new[] { 5, 7, 6, 8 })
        {
            now += 1;
            buffer.Take(sequence, [0], (number, _) => delivered.Add(number));
        }

        Assert.Equal([5, 6, 7, 8], delivered);
    }

    [Fact]
    public void Datagrams_in_order_pass_straight_through()
    {
        Take(5, 6, 7);

        Assert.Equal([5, 6, 7], _delivered);
        Assert.Equal(0, _buffer.Reordered);
    }

    [Fact]
    public void Swapped_datagrams_are_put_back_in_order()
    {
        Take(5, 7, 8, 6, 9);

        Assert.Equal([5, 6, 7, 8, 9], _delivered);
        Assert.Equal(2, _buffer.Reordered);
    }

    [Fact]
    public void The_order_survives_the_sequence_number_wrapping()
    {
        Take(65534, 0, 65535, 1);

        Assert.Equal([65534, 65535, 0, 1], _delivered);
    }

    [Fact]
    public void A_lost_datagram_is_given_up_on_when_the_window_fills()
    {
        Take(0);
        Take([.. Enumerable.Range(2, RtpReorderBuffer.Window)]);

        Assert.Equal([0, .. Enumerable.Range(2, RtpReorderBuffer.Window)], _delivered);
    }

    [Fact]
    public void A_duplicate_and_a_latecomer_are_not_delivered_again()
    {
        Take(5, 6, 6, 5, 7);

        Assert.Equal([5, 6, 7], _delivered);
    }

    [Fact]
    public void A_sender_that_restarts_far_away_is_followed()
    {
        Take(100, 101, 30000, 30001);

        Assert.Equal([100, 101, 30000, 30001], _delivered);
    }

    [Fact]
    public void Gaps_left_after_sorting_are_what_the_tracker_counts_as_lost()
    {
        var tracker = new RtpSequenceTracker();

        foreach (var sequence in new[] { 0, 2, 1, 3 })
        {
            _buffer.Take(sequence, [0], (delivered, _) => tracker.Take(delivered));
        }

        Assert.Equal(0, tracker.Lost);
    }
}
