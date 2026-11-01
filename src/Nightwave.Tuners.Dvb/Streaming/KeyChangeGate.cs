using Nightwave.Tuners.Dvb.Descrambling;
using Nightwave.Tuners.Dvb.Mpeg;

namespace Nightwave.Tuners.Dvb.Streaming;

/// <summary>
/// Keeps a late key from turning into noise. Where the broadcaster changes the parity, the packets
/// wait until a key is known to fit them.
/// </summary>
/// <remarks>
/// The key of a parity is reused for the same slot every other crypto period, so a key that is late
/// is not missing: the previous one is still there, it descrambles to noise, and nothing downstream
/// can tell, because the scrambling bits are cleared and the counters run on. Measured at a seven
/// second crypto period: keys arrive 30 to 390 ms ahead of the change, and about one change in
/// fifty they arrive up to 350 ms after it.
/// <para>
/// So nothing is decided by the clock or by whether a key is new: the first unit start after a
/// change is tried against the key, and must come out with a PES start code. That also leaves a
/// service whose key never changes alone. What waited goes out as soon as the key fits, whole and
/// in order, with its original timestamps. <see cref="Patience"/> stays below the time the freeze
/// frame waits for, so the two never overlap; past it the packets stay scrambled, which the feed
/// leaves out and the freeze frame covers, until a unit start fits again.
/// </para>
/// <para>
/// Called from the tuner's reading loop only. Sections do not pass through here: the next ECM
/// must not wait for the key of this one.
/// </para>
/// </remarks>
internal sealed class KeyChangeGate
{
    /// <summary>How long packets wait for a key before the outage is called one.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromMilliseconds(400);

    /// <summary>More than <see cref="Patience"/> of a whole 80 Mbit/s transponder.</summary>
    private const int MaxHeldBytes = 5 * 1024 * 1024 / TransportPacket.Size * TransportPacket.Size;

    private const int PidCount = 8192;

    private readonly TimeProvider _time;
    private readonly byte[] _parityOf = new byte[PidCount];
    private readonly byte[] _probe = new byte[TransportPacket.Size];
    private readonly Action<long, long> _onLate;
    private readonly Action<long> _onExpired;

    private byte[] _held = [];
    private int _heldLength;
    private GateState _state = GateState.Open;
    private int _fitted;
    private int _expected;
    private long _waitingSince;
    private bool _hasProbe;

    /// <param name="time">The clock.</param>
    /// <param name="onLate">Told how long a late key made packets wait, in milliseconds, and how many waited.</param>
    /// <param name="onExpired">Told how many packets waited in vain.</param>
    public KeyChangeGate(TimeProvider time, Action<long, long> onLate, Action<long> onExpired)
    {
        _time = time;
        _onLate = onLate;
        _onExpired = onExpired;
    }

    /// <summary>Takes packets that may go on, and whether they can be descrambled.</summary>
    public delegate void Release(Span<byte> packets, bool descramble);

    private enum GateState
    {
        /// <summary>The key of the current parity is known to fit.</summary>
        Open,

        /// <summary>The parity changed and no unit start has confirmed the key yet.</summary>
        Waiting,

        /// <summary>The wait ran out. Packets go on scrambled until a unit start fits.</summary>
        Keyless,
    }

    /// <param name="packets">Whole packets from the tuner, sections already forwarded.</param>
    /// <param name="descrambler">What tries a packet against its key.</param>
    /// <param name="pids">The pids that carry PES: only those have a start code to try.</param>
    /// <param name="release">Takes what may go on. Called in stream order.</param>
    public void Push(Span<byte> packets, IServiceDescrambler descrambler, IReadOnlySet<int> pids, Release release)
    {
        var start = 0;

        for (var offset = 0; offset + TransportPacket.Size <= packets.Length; offset += TransportPacket.Size)
        {
            var packet = packets.Slice(offset, TransportPacket.Size);
            var parity = TransportPacket.ScramblingControl(packet);

            if (parity < 2 || (packet[3] & 0x10) == 0)
            {
                continue;
            }

            var pid = TransportPacket.Pid(packet);

            if (!pids.Contains(pid))
            {
                continue;
            }

            // Per pid: the streams of a service change a few packets apart, and one that follows
            // late must not read as another change.
            var changed = _parityOf[pid] != parity;
            _parityOf[pid] = (byte)parity;

            if (_state == GateState.Open && changed && parity != _fitted)
            {
                release(packets[start..offset], true);
                start = offset;
                _state = GateState.Waiting;
                _expected = parity;
                _waitingSince = _time.GetTimestamp();
                _hasProbe = false;
            }
            else if (_state != GateState.Open && changed && parity != _expected)
            {
                // The next period began while this one was waited for. Joining a service, that
                // is the usual case: the first key is often the one for the period about to start.
                _expected = parity;
                _hasProbe = false;
            }

            if (_state == GateState.Open || parity != _expected || !TransportPacket.IsPayloadUnitStart(packet))
            {
                continue;
            }

            switch (descrambler.Probe(packet))
            {
                case KeyProbe.Fits when _state == GateState.Keyless:
                    release(packets[start..offset], false);
                    start = offset;
                    Fit();
                    break;
                case KeyProbe.Fits:
                    Flush(release);
                    Fit();
                    break;
                case KeyProbe.Wrong when !_hasProbe:
                    packet.CopyTo(_probe);
                    _hasProbe = true;
                    break;
            }
        }

        var rest = packets[start..];

        switch (_state)
        {
            case GateState.Open:
                release(rest, true);
                break;
            case GateState.Keyless:
                release(rest, false);
                break;
            default:
                Hold(rest);
                Settle(descrambler, release);
                break;
        }
    }

    private void Hold(Span<byte> packets)
    {
        var length = Math.Min(packets.Length, MaxHeldBytes - _heldLength);

        if (_held.Length < _heldLength + length)
        {
            Array.Resize(ref _held, Math.Min(MaxHeldBytes, Math.Max(_held.Length * 2, Math.Max(256 * 1024, _heldLength + length))));
        }

        packets[..length].CopyTo(_held.AsSpan(_heldLength));
        _heldLength += length;
    }

    /// <summary>Ends the wait where the key has come since, or where it has lasted long enough.</summary>
    private void Settle(IServiceDescrambler descrambler, Release release)
    {
        // A key arrives between the packets of a batch: the unit start that did not fit is tried again.
        if (_hasProbe && descrambler.Probe(_probe) == KeyProbe.Fits)
        {
            Flush(release);
            Fit();

            return;
        }

        if (_time.GetElapsedTime(_waitingSince) < Patience && _heldLength < MaxHeldBytes)
        {
            return;
        }

        _onExpired(_heldLength / TransportPacket.Size);
        release(_held.AsSpan(0, _heldLength), false);
        _heldLength = 0;
        _state = GateState.Keyless;
    }

    private void Flush(Release release)
    {
        if (_hasProbe)
        {
            _onLate((long)_time.GetElapsedTime(_waitingSince).TotalMilliseconds, _heldLength / TransportPacket.Size);
        }

        release(_held.AsSpan(0, _heldLength), true);
        _heldLength = 0;
    }

    private void Fit()
    {
        _fitted = _expected;
        _state = GateState.Open;
    }
}
