namespace Nightwave.Tuners.Dvb.Streaming;

/// <summary>
/// Hands out the numbers that tell descrambled services apart at the conditional access server:
/// the lowest free one, so a server that caps the adapter or demux index sees small numbers.
/// </summary>
internal sealed class DescramblerSlots
{
    private readonly SortedSet<int> _taken = [];
    private readonly Lock _gate = new();

    public int Rent()
    {
        lock (_gate)
        {
            var slot = 0;

            while (_taken.Contains(slot))
            {
                slot++;
            }

            _taken.Add(slot);

            return slot;
        }
    }

    public void Return(int slot)
    {
        lock (_gate)
        {
            _taken.Remove(slot);
        }
    }

    public int InUse
    {
        get
        {
            lock (_gate)
            {
                return _taken.Count;
            }
        }
    }
}
