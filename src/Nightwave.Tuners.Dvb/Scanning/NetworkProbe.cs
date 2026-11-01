using System.Diagnostics;
using Nightwave.Tuners.Dvb.Mpeg;
using Nightwave.Tuners.Dvb.Mpeg.Psi;
using Nightwave.Tuners.Dvb.Mpeg.Si;
using Nightwave.Tuners.Dvb.Transport;

namespace Nightwave.Tuners.Dvb.Scanning;

/// <summary>What a short look at a transponder found: whether it locked, and its network.</summary>
/// <param name="Signal">The tuner's last report, where it sends them.</param>
/// <param name="Network">The transport streams of the NIT actual; empty where none arrived in time.</param>
internal sealed record NetworkProbeResult(TunerSignal? Signal, IReadOnlyList<NetworkTransportStream> Network);

/// <summary>
/// Tunes a transponder only to find out whether it locks and which network it belongs to: the
/// PAT and the NIT actual, nothing else. What the detection of a device's inputs does on every
/// transponder it tries.
/// </summary>
internal static class NetworkProbe
{
    /// <summary>
    /// Starts <paramref name="transport"/> and reads the NIT actual. Fails with a
    /// <see cref="TunerTransportException"/> when the transponder does not lock; returns without
    /// a network when it locks but no NIT comes within <see cref="ScanTimings.Nit"/>. Stops as
    /// soon as <paramref name="enough"/> says what arrived is enough, or the NIT is complete.
    /// </summary>
    public static async Task<NetworkProbeResult> ReadAsync(
        ITsTransport transport,
        ScanTimings timings,
        CancellationToken cancellationToken,
        Func<IReadOnlyList<NetworkTransportStream>, bool>? enough = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(timings);

        var networkPid = TransportPacket.NitPid;
        await transport.StartAsync([TransportPacket.PatPid, networkPid], cancellationToken).ConfigureAwait(false);

        var sawData = false;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timings.Pat);
        using var watch = new LockWatch(transport, timings.Lock, () => Volatile.Read(ref sawData), deadline);

        var clock = Stopwatch.StartNew();
        var input = new byte[64 * 1024];
        var packets = new byte[input.Length + TransportPacket.Size];
        var aligner = new PacketAligner();
        var assemblers = new Dictionary<int, SectionAssembler>();
        var network = new SectionCollector<NetworkInformationTable>();
        int? announced = null;
        var satisfied = false;

        try
        {
            while (!network.IsComplete && !satisfied)
            {
                var read = await transport.ReadAsync(input, deadline.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new TunerTransportException("The device ended the stream while the transponder was being probed.");
                }

                var count = aligner.Push(input.AsSpan(0, read), packets);

                for (var offset = 0; offset < count; offset += TransportPacket.Size)
                {
                    var packet = packets.AsSpan(offset, TransportPacket.Size);
                    var pid = TransportPacket.Pid(packet);

                    if (pid != TransportPacket.PatPid && pid != networkPid)
                    {
                        continue;
                    }

                    if (!sawData)
                    {
                        // Data: the tuner is locked, whatever it reports. The NIT gets the time it may take.
                        Volatile.Write(ref sawData, true);
                        deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, (timings.Nit - clock.Elapsed).TotalMilliseconds)));
                    }

                    if (!assemblers.TryGetValue(pid, out var assembler))
                    {
                        assembler = new SectionAssembler();
                        assemblers[pid] = assembler;
                    }

                    var current = networkPid;
                    assembler.Push(packet, section =>
                    {
                        if (section[0] == ProgramAssociationTable.TableId && pid == TransportPacket.PatPid)
                        {
                            announced ??= ProgramAssociationTable.Parse(section)?.NetworkPid;
                        }
                        else if (section[0] == NetworkInformationTable.ActualTableId && pid == current
                            && NetworkInformationTable.Parse(section) is { } nit)
                        {
                            network.Add(nit, nit.Version, nit.SectionNumber, nit.LastSectionNumber);
                            satisfied |= enough?.Invoke([.. network.Sections.SelectMany(part => part.TransportStreams)]) == true;
                        }
                    });
                }

                // A PAT that names another pid for the NIT is followed.
                if (announced is { } other && other != networkPid)
                {
                    networkPid = other;
                    await transport.SetPidsAsync([TransportPacket.PatPid, networkPid], deadline.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!sawData)
            {
                throw LockWatch.Nothing(transport, watch.NoLock, timings.Lock, timings.Pat, "data");
            }

            // Locked, but the NIT did not come in time: what arrived is what there is.
        }

        return new NetworkProbeResult(transport.Status.Signal, [.. network.Sections.SelectMany(section => section.TransportStreams)]);
    }
}
