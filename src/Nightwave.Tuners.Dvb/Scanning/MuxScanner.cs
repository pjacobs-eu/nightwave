using System.Diagnostics;
using Nightwave.Tuners.Dvb.Mpeg;
using Nightwave.Tuners.Dvb.Mpeg.Psi;
using Nightwave.Tuners.Dvb.Mpeg.Si;
using Nightwave.Tuners.Dvb.Transport;

namespace Nightwave.Tuners.Dvb.Scanning;

/// <summary>How long a scan waits for each table. Shorter in tests.</summary>
internal sealed record ScanTimings
{
    /// <summary>
    /// How long a lock may take before the tuner's own "no lock" is believed. A Kathrein EXIP-418
    /// took up to 3.7 seconds on Astra 19.2°E after switching band or polarisation, and reports
    /// level 0 and no lock all the while; three seconds lost ZDF HD's neighbours from a scan.
    /// </summary>
    public TimeSpan Lock { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The pause before a transponder that gave nothing is tried again.</summary>
    public TimeSpan Retry { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The same while searching a cable or an aerial blindly. No input to switch there, and a
    /// frequency that locks does so within a second; every frequency without a signal costs this.
    /// </summary>
    public TimeSpan BlindLock { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Without a PAT in this time the tuner has no lock, or the transponder is empty. Longer than
    /// <see cref="Lock"/>, so a transponder that locks late still has time to send one.
    /// </summary>
    public TimeSpan Pat { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>For one batch of program maps. Each is repeated at least every 100 ms (ISO/IEC 13818-1, 2.4.4.1 note).</summary>
    public TimeSpan ProgramMaps { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>For the SDT once everything else is in; it repeats at least every 2 s (EN 300 468, 5.1.4).</summary>
    public TimeSpan Sdt { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>For the NIT from the start of the transponder; it repeats at least every 10 s.</summary>
    public TimeSpan Nit { get; init; } = TimeSpan.FromSeconds(12);

    /// <summary>For the whole transponder, whatever is still missing.</summary>
    public TimeSpan Transponder { get; init; } = TimeSpan.FromSeconds(25);
}

/// <summary>A service found on a transponder: its program map where it arrived, its SDT entry where there was one.</summary>
internal sealed record ScannedService(int ServiceId, int PmtPid, ProgramMapTable? ProgramMap, ServiceDescription? Description);

/// <summary>What one transponder said about itself.</summary>
internal sealed record MuxScanResult
{
    public required int TransportStreamId { get; init; }

    /// <summary>From the SDT; null where the transponder has none.</summary>
    public int? OriginalNetworkId { get; init; }

    public required IReadOnlyList<ScannedService> Services { get; init; }

    /// <summary>The transport streams of the NIT actual, where it arrived.</summary>
    public IReadOnlyList<NetworkTransportStream> Network { get; init; } = [];
}

/// <summary>
/// Reads the tables of one tuned transponder: the PAT, then the program map of every program in
/// it, the SDT actual for names and types, and the NIT actual for the other transponders. The
/// tuner is asked for the PMT pids in batches, since some SAT>IP servers pass only a few dozen
/// pids at once.
/// </summary>
internal static class MuxScanner
{
    private const int ProgramMapBatch = 24;

    /// <summary>
    /// Starts <paramref name="transport"/> and reads until everything is in or the time is up.
    /// Fails with a <see cref="TunerTransportException"/> when not even a PAT arrives, and as soon
    /// as <paramref name="lockWindow"/> (by default <see cref="ScanTimings.Lock"/>) is over when
    /// the tuner reports that it has no lock - with that report in <see cref="TunerTransportException.Signal"/>.
    /// </summary>
    /// <param name="expectedTransportStream">
    /// The transport stream the NIT says is here, where it said so. Tables of any other are what
    /// the device still had of the transponder before, and are not looked at.
    /// </param>
    public static async Task<MuxScanResult> ScanAsync(
        ITsTransport transport,
        ScanTimings timings,
        bool readNetwork,
        CancellationToken cancellationToken,
        TimeSpan? lockWindow = null,
        int? expectedTransportStream = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(timings);

        var state = new State(expectedTransportStream);
        var basePids = new HashSet<int> { TransportPacket.PatPid, TransportPacket.SdtPid };
        if (readNetwork)
        {
            basePids.Add(TransportPacket.NitPid);
        }

        await transport.StartAsync(basePids, cancellationToken).ConfigureAwait(false);

        // Any packet at all means the tuner has locked, whatever its last report says: the report
        // lags behind the lock, and a transponder whose PAT is still on its way is not one to give up on.
        var sawData = false;

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timings.Pat);
        using var watch = new LockWatch(transport, lockWindow ?? timings.Lock, () => Volatile.Read(ref sawData), deadline);

        var clock = Stopwatch.StartNew();
        var input = new byte[64 * 1024];
        var packets = new byte[input.Length + TransportPacket.Size];
        var aligner = new PacketAligner();
        var assemblers = new Dictionary<int, SectionAssembler>();
        var batch = new List<int>();
        var pending = new Queue<int>();
        var batchStarted = TimeSpan.Zero;
        TimeSpan? programMapsDoneAt = null;
        var networkPid = TransportPacket.NitPid;
        var patTaken = 0;

        try
        {
            while (true)
            {
                var read = await transport.ReadAsync(input, deadline.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new TunerTransportException("The device ended the stream during the scan.");
                }

                Volatile.Write(ref sawData, true);

                var count = aligner.Push(input.AsSpan(0, read), packets);

                for (var offset = 0; offset < count; offset += TransportPacket.Size)
                {
                    var packet = packets.AsSpan(offset, TransportPacket.Size);
                    var pid = TransportPacket.Pid(packet);

                    if (!state.Wants(pid, networkPid))
                    {
                        continue;
                    }

                    if (!assemblers.TryGetValue(pid, out var assembler))
                    {
                        assembler = new SectionAssembler();
                        assemblers[pid] = assembler;
                    }

                    assembler.Push(packet, section => state.Take(pid, networkPid, section));
                }

                if (state.PatGeneration != patTaken && state.Pat is { } pat)
                {
                    if (patTaken == 0)
                    {
                        // The PAT is in: the tuner is locked. The rest gets the time the transponder has left.
                        deadline.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, (timings.Transponder - clock.Elapsed).TotalMilliseconds)));
                    }
                    else
                    {
                        // A PAT of another transport stream: the first was left over from what the
                        // tuner was tuned to before (see State.Take). Its program maps are not wanted.
                        pending.Clear();
                        batch.Clear();
                        programMapsDoneAt = null;
                    }

                    patTaken = state.PatGeneration;

                    if (pat.NetworkPid is { } announced && announced != networkPid && readNetwork)
                    {
                        networkPid = announced;
                        basePids.Add(announced);
                    }

                    foreach (var pmtPid in pat.Programs.Values.Distinct())
                    {
                        pending.Enqueue(pmtPid);
                    }
                }

                if (state.Pat is not null)
                {
                    var batchDone = batch.All(state.HasProgramMapsOn) || clock.Elapsed - batchStarted > timings.ProgramMaps;

                    if (batchDone && pending.Count > 0)
                    {
                        batch.Clear();

                        while (batch.Count < ProgramMapBatch && pending.Count > 0)
                        {
                            batch.Add(pending.Dequeue());
                        }

                        state.ProgramMapPids.UnionWith(batch);
                        batchStarted = clock.Elapsed;

                        await transport.SetPidsAsync([.. basePids, .. batch], deadline.Token).ConfigureAwait(false);
                    }
                    else if (batchDone && pending.Count == 0)
                    {
                        programMapsDoneAt ??= clock.Elapsed;
                    }
                }

                if (programMapsDoneAt is { } mapsDone
                    && (state.SdtComplete || clock.Elapsed - mapsDone > timings.Sdt)
                    && (!readNetwork || state.NitComplete || clock.Elapsed > timings.Nit))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (state.Pat is null)
            {
                throw LockWatch.Nothing(transport, watch.NoLock, lockWindow ?? timings.Lock, timings.Pat, "program association table");
            }

            // Out of time with the PAT in: what arrived is what there is.
        }

        var result = state.Result();

        // A PAT and not one other table in all that time is no transponder, it is a PAT left over
        // from the one before, on a stream that went quiet after it. Kept, its services would
        // replace the good channels of that transponder and this one's would be lost.
        if (result.Services.Count > 0
            && result.Network.Count == 0
            && result.Services.All(service => service.ProgramMap is null && service.Description is null))
        {
            throw new TunerTransportException(
                $"Only a program association table of transport stream {result.TransportStreamId} arrived, and nothing else in {clock.Elapsed.TotalSeconds:0} seconds.");
        }

        return result;
    }

    /// <summary>The tables of one transponder as they come in.</summary>
    /// <remarks>
    /// The first tables after a tune can be the previous transponder's: a Kathrein EXIP-418 tuned
    /// from 11836 H to 12304 H on the same tuner sent a PAT of 11836 H's transport stream first.
    /// So the PAT that counts is the last one, a PAT of another transport stream starts the
    /// transponder over, and an SDT counts only for the transport stream it names - the leftover
    /// SDT of one transponder next to the PAT of another made every service in it a channel of the
    /// wrong transponder.
    /// </remarks>
    private sealed class State(int? expectedTransportStream)
    {
        private readonly Dictionary<int, SectionCollector<ServiceDescriptionTable>> _sdt = new();
        private readonly SectionCollector<NetworkInformationTable> _nit = new();
        private readonly Dictionary<int, ProgramMapTable> _programMaps = new();
        private SectionCollector<ProgramAssociationTable> _pat = new();
        private int? _patTransportStream;

        public ProgramAssociationTable? Pat { get; private set; }

        /// <summary>Raised whenever <see cref="Pat"/> is complete for another transport stream than before.</summary>
        public int PatGeneration { get; private set; }

        public HashSet<int> ProgramMapPids { get; } = [];

        public bool SdtComplete => Pat is not null && _sdt.TryGetValue(Pat.TransportStreamId, out var sdt) && sdt.IsComplete;

        public bool NitComplete => _nit.IsComplete;

        public bool Wants(int pid, int networkPid) =>
            pid is TransportPacket.PatPid or TransportPacket.SdtPid || pid == networkPid || ProgramMapPids.Contains(pid);

        /// <summary>Whether every program whose map is on <paramref name="pid"/> has its map.</summary>
        public bool HasProgramMapsOn(int pid) =>
            Pat is null || Pat.Programs.Where(program => program.Value == pid).All(program => _programMaps.ContainsKey(program.Key));

        public void Take(int pid, int networkPid, ReadOnlySpan<byte> section)
        {
            switch (section[0])
            {
                case ProgramAssociationTable.TableId when pid == TransportPacket.PatPid:
                    if (ProgramAssociationTable.Parse(section) is not { } pat
                        || (expectedTransportStream is { } expected && pat.TransportStreamId != expected))
                    {
                        break;
                    }

                    if (_patTransportStream != pat.TransportStreamId)
                    {
                        _patTransportStream = pat.TransportStreamId;
                        _pat = new SectionCollector<ProgramAssociationTable>();

                        if (Pat is not null)
                        {
                            Pat = null;
                            _programMaps.Clear();
                            ProgramMapPids.Clear();
                        }
                    }

                    if (_pat.Add(pat, pat.Version, pat.SectionNumber, pat.LastSectionNumber))
                    {
                        PatGeneration++;
                        Pat = new ProgramAssociationTable
                        {
                            TransportStreamId = pat.TransportStreamId,
                            Version = pat.Version,
                            SectionNumber = 0,
                            LastSectionNumber = 0,
                            Programs = _pat.Sections.SelectMany(part => part.Programs).DistinctBy(program => program.Key).ToDictionary(),
                            NetworkPid = _pat.Sections.Select(part => part.NetworkPid).FirstOrDefault(networkPid => networkPid is not null),
                        };
                    }

                    break;

                case ProgramMapTable.TableId when ProgramMapPids.Contains(pid):
                    if (ProgramMapTable.Parse(section) is { } programMap)
                    {
                        _programMaps[programMap.ProgramNumber] = programMap;
                    }

                    break;

                case ServiceDescriptionTable.ActualTableId when pid == TransportPacket.SdtPid:
                    if (ServiceDescriptionTable.Parse(section) is { } sdt)
                    {
                        if (!_sdt.TryGetValue(sdt.TransportStreamId, out var collector))
                        {
                            collector = new SectionCollector<ServiceDescriptionTable>();
                            _sdt[sdt.TransportStreamId] = collector;
                        }

                        collector.Add(sdt, sdt.Version, sdt.SectionNumber, sdt.LastSectionNumber);
                    }

                    break;

                case NetworkInformationTable.ActualTableId when pid == networkPid:
                    if (NetworkInformationTable.Parse(section) is { } nit)
                    {
                        _nit.Add(nit, nit.Version, nit.SectionNumber, nit.LastSectionNumber);
                    }

                    break;
            }
        }

        /// <remarks>
        /// A service the SDT describes and the PAT leaves out is in it too, without a program map.
        /// That is how a channel on a break looks: Sky takes Sky Sport Bundesliga 3 out of the PAT
        /// of 12304 H between matches and leaves it in the SDT as not running. The feed finds its
        /// program map in the PAT when it is tuned and on the air again.
        /// </remarks>
        public MuxScanResult Result()
        {
            var pat = Pat!;
            var sdt = _sdt.TryGetValue(pat.TransportStreamId, out var collector) ? collector.Sections.ToList() : [];
            var descriptions = sdt
                .SelectMany(section => section.Services)
                .DistinctBy(service => service.ServiceId)
                .ToDictionary(service => service.ServiceId);

            var described = descriptions.Keys
                .Where(serviceId => !pat.Programs.ContainsKey(serviceId))
                .Select(serviceId => KeyValuePair.Create(serviceId, 0));

            return new MuxScanResult
            {
                TransportStreamId = pat.TransportStreamId,
                OriginalNetworkId = sdt.FirstOrDefault()?.OriginalNetworkId,
                Services =
                [
                    .. pat.Programs
                        .Concat(described)
                        .OrderBy(program => program.Key)
                        .Select(program => new ScannedService(
                            program.Key,
                            program.Value,
                            _programMaps.GetValueOrDefault(program.Key),
                            descriptions.GetValueOrDefault(program.Key))),
                ],
                Network = [.. _nit.Sections.SelectMany(section => section.TransportStreams)],
            };
        }
    }
}

/// <summary>
/// Collects the sections of one table (EN 300 468, 5.1.2): complete once every section number
/// up to the last one of the same version is in. A new version starts over.
/// </summary>
internal sealed class SectionCollector<T>
{
    private readonly SortedDictionary<int, T> _sections = new();
    private int? _version;
    private int _last;

    public bool IsComplete { get; private set; }

    public IEnumerable<T> Sections => _sections.Values;

    /// <summary>Adds a section. Returns true when this one completed the table.</summary>
    public bool Add(T section, int version, int number, int last)
    {
        if (_version != version)
        {
            _sections.Clear();
            _version = version;
            IsComplete = false;
        }

        _last = last;

        if (!_sections.TryAdd(number, section) || IsComplete)
        {
            return false;
        }

        IsComplete = Enumerable.Range(0, _last + 1).All(_sections.ContainsKey);

        return IsComplete;
    }
}
