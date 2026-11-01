namespace Nightwave.Tuners.Dvb.Transport;

/// <summary>
/// Puts RTP datagrams back in sequence (RFC 3550, 5.1). A datagram ahead of what is expected is
/// held until the missing one arrives or <see cref="Window"/> datagrams have piled up behind it;
/// then the missing one counts as lost and delivery moves on.
/// </summary>
/// <remarks>Used from the one loop that receives, so it takes no locks for the sequence itself.</remarks>
internal sealed class RtpReorderBuffer
{
    /// <summary>Datagrams carry seven packets each, so this is a few milliseconds of an HD transponder.</summary>
    public const int Window = 32;

    private const int Modulus = 1 << 16;

    /// <summary>The datagrams a sender needs to have jumped by for it to be a restart rather than disorder.</summary>
    private const int RestartDistance = 1024;

    public delegate void Deliver(int sequence, ReadOnlySpan<byte> payload);

    private readonly Dictionary<int, byte[]> _held = [];
    private int _expected = -1;
    private long _reordered;

    /// <summary>How many datagrams arrived out of order and were put back.</summary>
    public long Reordered => Volatile.Read(ref _reordered);

    public void Take(int sequence, ReadOnlySpan<byte> payload, Deliver deliver)
    {
        sequence &= Modulus - 1;

        if (_expected < 0 || sequence == _expected)
        {
            deliver(sequence, payload);
            _expected = (sequence + 1) % Modulus;
            DrainInOrder(deliver);

            return;
        }

        var ahead = (sequence - _expected + Modulus) % Modulus;

        if (ahead >= Modulus / 2)
        {
            // Behind what was already delivered: it came too late to sort in, or twice. Handing
            // it on now would put old packets into the middle of the stream.
            if (Modulus - ahead > RestartDistance)
            {
                Restart(sequence, payload, deliver);
            }

            return;
        }

        if (ahead > RestartDistance)
        {
            Restart(sequence, payload, deliver);

            return;
        }

        _held.TryAdd(sequence, payload.ToArray());

        if (_held.Count >= Window)
        {
            GiveUpOnGap(deliver);
        }
    }

    private void Restart(int sequence, ReadOnlySpan<byte> payload, Deliver deliver)
    {
        Flush(deliver);
        deliver(sequence, payload);
        _expected = (sequence + 1) % Modulus;
    }

    private void GiveUpOnGap(Deliver deliver)
    {
        _expected = _held.Keys.MinBy(held => (held - _expected + Modulus) % Modulus);
        DrainInOrder(deliver);
    }

    private void DrainInOrder(Deliver deliver)
    {
        while (_held.Remove(_expected, out var next))
        {
            Interlocked.Increment(ref _reordered);
            deliver(_expected, next);
            _expected = (_expected + 1) % Modulus;
        }
    }

    private void Flush(Deliver deliver)
    {
        while (_held.Count > 0)
        {
            GiveUpOnGap(deliver);
        }
    }
}
