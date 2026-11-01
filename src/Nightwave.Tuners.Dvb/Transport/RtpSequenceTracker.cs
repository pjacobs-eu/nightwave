namespace Nightwave.Tuners.Dvb.Transport;

/// <summary>
/// Counts RTP datagrams that did not arrive, from the gaps in their 16 bit sequence numbers
/// (RFC 3550, 5.1). A datagram that comes late or twice is not counted as a loss, and does not
/// move the count back: over UDP, order is not promised, and a jump back of more than half the
/// number space is taken to be one of those rather than 65000 losses.
/// </summary>
/// <remarks>
/// Written from the one loop that receives, read from anywhere: the counts are read with
/// <see cref="Volatile"/>.
/// </remarks>
internal sealed class RtpSequenceTracker
{
    private const int Modulus = 1 << 16;

    private int _expected = -1;
    private long _received;
    private long _lost;

    public long Received => Volatile.Read(ref _received);

    public long Lost => Volatile.Read(ref _lost);

    /// <summary>Takes the sequence number of a datagram that arrived. Returns how many went missing before it.</summary>
    public int Take(int sequence)
    {
        sequence &= Modulus - 1;
        Volatile.Write(ref _received, _received + 1);

        if (_expected < 0)
        {
            _expected = (sequence + 1) % Modulus;
            return 0;
        }

        var gap = (sequence - _expected + Modulus) % Modulus;

        if (gap >= Modulus / 2)
        {
            // Behind what was expected: late, or a duplicate.
            return 0;
        }

        _expected = (sequence + 1) % Modulus;

        if (gap > 0)
        {
            Volatile.Write(ref _lost, _lost + gap);
        }

        return gap;
    }
}
