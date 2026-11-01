using System.Collections.Concurrent;
using Nightwave.Tuners.Dvb.Descrambling.Csa;

namespace Nightwave.Tuners.Dvb.Descrambling;

/// <summary>
/// The newest ECM of each ECM pid, kept from the moment the tuner is asked for them. The program
/// map, the connection to the server and the CA PMT all come before the server's filter, and the
/// ECM sent meanwhile is the one the card can answer at once - rather than after the broadcaster
/// has sent the next one.
/// </summary>
/// <remarks>
/// <see cref="Keep"/> is called from the one loop that reads the stream; <see cref="TryTake"/>
/// from anywhere.
/// </remarks>
internal sealed class RecentEcms
{
    private readonly TimeProvider _time;

    /// <summary>
    /// How old a kept ECM may be when it is handed over. Well inside a crypto period - seven to
    /// ten seconds at the shortest - so the key it produces is still the one in use.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(3);

    // Loop only.
    private readonly Dictionary<int, DemuxSectionAssembler> _assemblers = [];

    private readonly ConcurrentDictionary<int, (byte[] Section, long At)> _newest = new();

    public RecentEcms(TimeProvider time) => _time = time;

    /// <summary>Takes one 188 byte packet of an ECM pid; a whole ECM in it replaces the last.</summary>
    public void Keep(int pid, ReadOnlySpan<byte> packet)
    {
        if (!_assemblers.TryGetValue(pid, out var assembler))
        {
            assembler = new DemuxSectionAssembler();
            _assemblers[pid] = assembler;
        }

        assembler.Feed(packet, (Ecms: this, Pid: pid), static (section, state) =>
        {
            if (IcamEcm.ParityOf(section) >= 0)
            {
                state.Ecms._newest[state.Pid] = (section.ToArray(), state.Ecms._time.GetTimestamp());
            }
        });
    }

    /// <summary>The ECM kept for a pid, once, where it arrived no longer than <see cref="MaxAge"/> ago.</summary>
    public bool TryTake(int pid, out byte[] section)
    {
        if (_newest.TryRemove(pid, out var kept) && _time.GetElapsedTime(kept.At) <= MaxAge)
        {
            section = kept.Section;
            return true;
        }

        section = [];
        return false;
    }
}
