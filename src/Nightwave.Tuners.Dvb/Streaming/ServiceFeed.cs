using System.Diagnostics;
using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb.Channels;
using Nightwave.Tuners.Dvb.Descrambling;
using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.Mpeg;
using Nightwave.Tuners.Dvb.Mpeg.Psi;
using Microsoft.Extensions.Logging;

namespace Nightwave.Tuners.Dvb.Streaming;

/// <summary>How long a tune may take before it is called a failure. Shorter in tests.</summary>
internal sealed record FeedTimings
{
    /// <summary>From the tuner starting to the service's program map arriving.</summary>
    public TimeSpan ProgramMap { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>From the descrambler opening to its first control word.</summary>
    public TimeSpan Keys { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long a tuned transponder may deliver nothing before it counts as lost. A PAT comes at
    /// least every half second on every transponder, so a few seconds of silence is no pause
    /// in the broadcast - it is a server that dropped the session without saying so, which over
    /// UDP is the only way it ever says it.
    /// </summary>
    public TimeSpan Stall { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>
    /// How long a new reader waits for a picture it can start on before it is handed the stream
    /// as it is. Every broadcast carries one every couple of seconds, so a wait this long is a
    /// broadcaster that leaves the indicator out rather than a long group of pictures.
    /// </summary>
    public TimeSpan CleanStart { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long a lost tuner is tuned again before its viewers are told the channel is gone.</summary>
    public TimeSpan Recovery { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The waits between attempts to tune a lost transponder again; the last one repeats.</summary>
    public IReadOnlyList<TimeSpan> RecoveryBackoff { get; init; } =
        [TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15)];
}

/// <summary>
/// One service cut out of a tuned multiplex, shared by everybody watching it. Sees every packet
/// the tuner delivers and passes on only what a player of this service needs: a PAT that lists
/// this service alone, its program map, its streams and its clock. ECMs, EMMs, the CAT, the
/// other services and the SI tables stay behind.
/// </summary>
/// <remarks>
/// A scrambled service gets a descrambler, which sees the packets before they are filtered -
/// it needs the ECMs that are about to be dropped. Each service works on its own copy of the
/// packets, so one service's descrambler never touches what another service on the same
/// transponder passes on.
/// </remarks>
internal sealed partial class ServiceFeed : IAsyncDisposable
{
    private static readonly TimeSpan DropWarningInterval = TimeSpan.FromSeconds(10);

    private readonly TunerDevice _device;
    private readonly string _transponder;
    private readonly IServiceDescramblerFactory _descramblers;
    private readonly DescramblerSlots _slots;
    private readonly Action _pidsChanged;
    private readonly FeedTimings _timings;
    private readonly ILogger _logger;
    private readonly TaskCompletionSource _mapArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // When the feed was made, and when its first packet, its program map and its readiness came;
    // timestamps, 0 until it happened. What a slow channel was waiting for, said once it is open.
    private readonly long _createdAt = Stopwatch.GetTimestamp();
    private long _firstPacketAt;
    private long _mapAt;
    private long _readyAt;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();
    private readonly List<ServiceReader> _readers = [];
    private readonly SectionAssembler _patAssembler = new();
    private readonly int _serviceId;

    /// <summary>
    /// Where the tables sit in the batch being built, while it is being built: a reader that has
    /// not started yet is given them even though it is given nothing else from before its first
    /// picture, so that its PAT and its program map arrive without a gap in between.
    /// </summary>
    private readonly List<int> _tablesInBatch = [];

    // Everything below up to the descrambler is only touched from the tuner's reading loop.
    private SectionAssembler _pmtAssembler = new();
    private int _transportStreamId;
    private int _pmtPid;
    private int _patVersion;
    private int _patContinuity;
    private int _lastPmtContinuity = -1;
    private byte[] _patPacket;
    private byte[] _work = [];
    private readonly KeyChangeGate _keyGate;
    private readonly KeyChangeGate.Release _release;

    /// <summary>The streams a key can be tried on: pictures and sound are PES, whatever else a service carries may not be.</summary>
    private volatile HashSet<int> _gatedPids = [];

    /// <summary>The newest ECM of each ECM pid until the first key, for the server's first filter.</summary>
    private readonly RecentEcms _recentEcms = new(TimeProvider.System);
    private DateTimeOffset _lastDropWarning = DateTimeOffset.MinValue;
    private long _stillScrambled;
    private long _stillScrambledReported;

    /// <summary>The pid whose packets are being left out for want of a key right now, or -1. Loop only.</summary>
    private int _waitingForKeyOn = -1;
    private long _beforeKeys;
    private DateTimeOffset _lastScrambledReport = DateTimeOffset.MinValue;

    // Read by the loop, replaced from elsewhere: whole objects swapped, never changed in place.
    private volatile HashSet<int> _outputPids;
    private volatile HashSet<int> _ecmPids;
    private volatile ProgramMapTable? _programMap;
    private volatile IServiceDescrambler? _descrambler;
    private volatile Exception? _fault;
    private volatile bool _scrambled;
    private volatile TunerException? _descramblingProblem;

    /// <summary>The service's video pid, as its program map names it; -1 for a radio service.</summary>
    private volatile int _videoPid = -1;

    /// <summary>Whether the card has ever answered. Until it has, scrambled packets are the join.</summary>
    private volatile bool _hasKeys;

    private IReadOnlyCollection<int> _wanted = [];
    private Task? _opening;
    private int? _slot;
    private bool _disposed;

    /// <summary>How many readers are still waiting for a picture to start on. Under <see cref="_gate"/>.</summary>
    private int _waitingReaders;

    public ServiceFeed(
        StoredChannel channel,
        TunerDevice device,
        string transponder,
        IServiceDescramblerFactory descramblers,
        DescramblerSlots slots,
        Action pidsChanged,
        FeedTimings timings,
        ILogger logger)
    {
        Channel = channel;
        _device = device;
        _transponder = transponder;
        _descramblers = descramblers;
        _slots = slots;
        _pidsChanged = pidsChanged;
        _timings = timings;
        _logger = logger;
        _release = Release;
        _keyGate = new KeyChangeGate(
            TimeProvider.System,
            (milliseconds, count) => LogKeyLate(Channel.Name, milliseconds, count),
            count => LogKeyTooLate(Channel.Name, (long)KeyChangeGate.Patience.TotalMilliseconds, count));

        _serviceId = channel.ServiceId;
        _transportStreamId = channel.TransportStreamId;
        _pmtPid = channel.PmtPid > 0 ? channel.PmtPid : -1;

        // Known from the scan, so the tuner can be asked for them before the PAT confirms them.
        var known = channel.Pids.ToHashSet();
        if (channel.PcrPid is > 0 and < TransportPacket.NullPid)
        {
            known.Add(channel.PcrPid);
        }

        _outputPids = known;
        _ecmPids = channel.Scrambled ? [.. channel.EcmPids] : [];
        _patPacket = BuildPatPacket();
        _wanted = ComputeWanted();
    }

    public StoredChannel Channel { get; }

    /// <summary>
    /// How long, from when this feed was made, its first packet, its program map and its
    /// readiness took, in milliseconds; -1 for what has not happened.
    /// </summary>
    public (long FirstPacket, long ProgramMap, long Ready) Timings
    {
        get
        {
            long Since(long at) => at == 0 ? -1 : (long)Stopwatch.GetElapsedTime(_createdAt, at).TotalMilliseconds;

            return (Since(Volatile.Read(ref _firstPacketAt)), Since(Interlocked.Read(ref _mapAt)), Since(Interlocked.Read(ref _readyAt)));
        }
    }

    public Exception? Fault => _fault;

    public bool IsFaulted => _fault is not null;

    /// <summary>
    /// What keeps the service from being descrambled right now, as the descrambler reports it -
    /// the descrambling server gone, a card that does not answer - while the stream itself
    /// carries on.
    /// </summary>
    public TunerException? DescramblingProblem => _descramblingProblem;

    /// <summary>Whether the service is scrambled: the scan said so, or its packets did.</summary>
    public bool IsScrambled => _scrambled || Channel.Scrambled;

    /// <summary>Whether anything could descramble it: a descrambling server is set up.</summary>
    public bool CanDescramble => _descramblers.IsConfigured;

    /// <summary>What the descrambler says about itself, where there is one that can.</summary>
    public DescramblingStatus? DescramblingStatus => (_descrambler as IServiceDescramblerDiagnostics)?.Status;

    /// <summary>
    /// Packets left out because they were still scrambled after the descrambler had them, once
    /// the card had answered at all. What was dropped before its first key is the price of
    /// joining a scrambled service rather than a fault, and is counted apart from this.
    /// </summary>
    public long StillScrambledPackets => Interlocked.Read(ref _stillScrambled);

    /// <summary>The elementary stream pids that are passed on to the viewers.</summary>
    public IReadOnlyCollection<int> OutputPids => _outputPids;

    /// <summary>The program map as last received, where one was.</summary>
    public ProgramMapTable? ProgramMap => _programMap;

    /// <summary>How many viewers hold this feed. Counted by the pool, under its lock.</summary>
    public int References { get; set; }

    /// <summary>The pids this service needs from the tuner right now.</summary>
    public IReadOnlyCollection<int> WantedPids
    {
        get
        {
            lock (_gate)
            {
                return _wanted;
            }
        }
    }

    /// <summary>
    /// Waits until the service can be watched: its program map has arrived and, where it is
    /// scrambled, the card has sent its first key. Fails with the reason when it cannot.
    /// </summary>
    public async Task WaitReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _mapArrived.Task.WaitAsync(_timings.ProgramMap + _device.TuneAllowance, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var error = DvbErrors.NoSignal(Channel.Name, _transponder, _device.Label, _timings.ProgramMap);
            Fail(error);
            throw _fault ?? error;
        }

        try
        {
            // The key wait fails the feed by itself; this only guards against it never reporting.
            await _ready.Task.WaitAsync(_timings.Keys + TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var error = DvbErrors.NoKeys(Channel.Name, _timings.Keys);
            Fail(error);
            throw _fault ?? error;
        }
    }

    /// <summary>A new reader of this service. Starts with a PAT and the program map, so a player can begin at once.</summary>
    public ServiceReader Subscribe()
    {
        if (_fault is { } fault)
        {
            throw fault;
        }

        var reader = new ServiceReader(Unsubscribe);
        var start = new List<byte[]>();

        var pat = (byte[])_patPacket.Clone();
        TransportPacket.WriteHeader(pat, TransportPacket.PatPid, payloadUnitStart: true, (Volatile.Read(ref _patContinuity) - 1) & 0x0F);
        start.Add(pat);

        if (_programMap is { } programMap && _pmtPid > 0)
        {
            // Numbered so the next program map packet from the tuner follows on without a gap.
            var count = SectionPacketizer.PacketCount(programMap.Section.Length);
            var first = (_lastPmtContinuity - count + 1) & 0x0F;
            SectionPacketizer.Packetize(programMap.Section, _pmtPid, first, start);
        }

        reader.Offer(start.SelectMany(packet => packet).ToArray());

        lock (_gate)
        {
            if (_fault is { } late)
            {
                reader.Complete(late);
            }
            else
            {
                // A broadcast is joined in the middle of a picture. This reader is given the
                // stream from the next picture that stands on its own, so what it hands a
                // decoder begins with a whole picture and the parameter sets that describe it,
                // rather than with the tail of one whose beginning nobody ever saw.
                if (_videoPid >= 0)
                {
                    reader.WaitsForPicture = true;
                    reader.PictureWaitUntil = DateTimeOffset.UtcNow + _timings.CleanStart;
                    _waitingReaders++;
                }

                _readers.Add(reader);
            }
        }

        return reader;
    }

    /// <summary>
    /// Sends this service's PAT on its own, while the tuner is being brought back. A reader that
    /// hears nothing gives up - the worker after fifteen seconds - and a tuner that lost its
    /// server may need longer than that to be back; a PAT a second keeps the reader waiting for
    /// the picture rather than calling the channel gone. Called from the tuner's reading loop,
    /// which is not reading meanwhile.
    /// </summary>
    public void KeepAlive()
    {
        if (_fault is not null || _disposed)
        {
            return;
        }

        var pat = new byte[TransportPacket.Size];
        _patPacket.CopyTo(pat, 0);
        TransportPacket.WriteHeader(pat, TransportPacket.PatPid, true, _patContinuity);
        _patContinuity = (_patContinuity + 1) & 0x0F;

        // Also as a table for a reader still waiting for a picture: it is one, and its counter
        // has to reach that reader like every other.
        Dispatch(pat, tables: pat);
    }

    /// <summary>
    /// Takes whole packets from the tuner. Called from the tuner's one reading loop and from
    /// nowhere else.
    /// </summary>
    public void Process(ReadOnlySpan<byte> packets)
    {
        if (_fault is not null || _disposed || packets.IsEmpty)
        {
            return;
        }

        if (_firstPacketAt == 0)
        {
            _firstPacketAt = Stopwatch.GetTimestamp();
        }

        if (_work.Length < packets.Length)
        {
            _work = new byte[packets.Length];
        }

        var work = _work.AsSpan(0, packets.Length);
        packets.CopyTo(work);

        // Before anything is dropped: the descrambler needs the ECMs, and clears the payloads in place.
        if (!_hasKeys)
        {
            KeepRecentEcms(work);
        }

        if (_descrambler is { } descrambler && _hasKeys)
        {
            // The ECMs go at once, whatever the picture is waiting for: the next key depends on them.
            descrambler.ForwardSections(work);
            _keyGate.Push(work, descrambler, _gatedPids, _release);
        }
        else
        {
            _descrambler?.Process(work);
            Deliver(work);
        }
    }

    private void Release(Span<byte> packets, bool descramble)
    {
        if (packets.IsEmpty)
        {
            return;
        }

        if (descramble)
        {
            _descrambler?.Descramble(packets);
        }

        Deliver(packets);
    }

    /// <summary>Passes on what belongs to the service, descrambled by now where it could be.</summary>
    private void Deliver(Span<byte> work)
    {
        var output = new byte[work.Length];
        var written = 0;
        var randomAccess = -1;

        _tablesInBatch.Clear();

        for (var offset = 0; offset + TransportPacket.Size <= work.Length; offset += TransportPacket.Size)
        {
            var packet = work.Slice(offset, TransportPacket.Size);
            var pid = TransportPacket.Pid(packet);

            if (pid == TransportPacket.PatPid)
            {
                _patAssembler.Push(packet, OnPatSection);

                if (TransportPacket.IsPayloadUnitStart(packet))
                {
                    // Every PAT the tuner delivers is answered with this service's own, at the same rate.
                    TransportPacket.WriteHeader(_patPacket, TransportPacket.PatPid, true, _patContinuity);
                    _patContinuity = (_patContinuity + 1) & 0x0F;
                    _tablesInBatch.Add(written);
                    _patPacket.CopyTo(output.AsSpan(written));
                    written += TransportPacket.Size;
                }

                continue;
            }

            if (pid == _pmtPid)
            {
                _lastPmtContinuity = TransportPacket.ContinuityCounter(packet);
                _pmtAssembler.Push(packet, OnPmtSection);
                _tablesInBatch.Add(written);
                packet.CopyTo(output.AsSpan(written));
                written += TransportPacket.Size;
                continue;
            }

            // Read for every packet: a program map earlier in this same batch may have changed it.
            if (!_outputPids.Contains(pid))
            {
                continue;
            }

            if (!_scrambled && TransportPacket.ScramblingControl(packet) != 0 && _programMap is { } map)
            {
                // Scrambled without saying so in the program map: some operators leave the CA
                // descriptors out. The packets say it all the same.
                LogScramblingBits(Channel.Name, pid);
                StartDescrambling(map);
            }

            // Still scrambled after the descrambler had it: no key yet for its parity - the card
            // answers one parity first and the other a crypto period later - or a key that did
            // not arrive in time. Passed on, it is noise inside a copied picture, and a browser's
            // decoder gives up on the whole stream for it where ffmpeg only complains. Left out,
            // it is a gap the decoder bridges at the next keyframe.
            if (_scrambled && TransportPacket.ScramblingControl(packet) != 0)
            {
                // Before the card has answered at all, this is the service being joined rather
                // than anything going wrong: nobody has the first key yet. Counted apart, and
                // said once when the key arrives.
                if (_hasKeys)
                {
                    _stillScrambled++;

                    if (_waitingForKeyOn < 0)
                    {
                        _waitingForKeyOn = pid;
                        if (_descrambler is IServiceDescramblerDiagnostics diagnostics)
                        {
                            LogKeyMissing(Channel.Name, pid, diagnostics.DescribeMissingKey(pid, TransportPacket.ScramblingControl(packet) == 3));
                        }
                    }
                }
                else
                {
                    _beforeKeys++;
                }

                continue;
            }

            // Clear again on the pid that was waiting: the wait for its key is over. Only that pid
            // ends it, so a track that stays scrambled beside a clear picture is said once.
            if (pid == _waitingForKeyOn)
            {
                _waitingForKeyOn = -1;
            }

            if (randomAccess < 0 && pid == _videoPid && TransportPacket.IsRandomAccessPoint(packet))
            {
                randomAccess = written;
            }

            packet.CopyTo(output.AsSpan(written));
            written += TransportPacket.Size;
        }

        if (written > 0)
        {
            var waiting = Volatile.Read(ref _waitingReaders) > 0;

            // Spelled out rather than handed to a conditional: a null there turns into an empty
            // array on its way to a ReadOnlyMemory, and an empty clean start is one every reader
            // starts on.
            ReadOnlyMemory<byte>? cleanStart = null;

            if (waiting && randomAccess >= 0)
            {
                cleanStart = StartFrom(output, randomAccess, written);
            }

            Dispatch(output.AsMemory(0, written), cleanStart, waiting ? Tables(output, written) : default);
        }

        ReportStillScrambled();
    }

    /// <summary>
    /// The tables of the batch, for a reader that is waiting for a picture and is given nothing
    /// else of it: a PAT and a program map it never saw would be a gap in the counters of those
    /// pids, which is a demuxer complaining about lost packets that were never lost.
    /// </summary>
    private ReadOnlyMemory<byte> Tables(byte[] output, int written)
    {
        if (_tablesInBatch.Count == 0)
        {
            return default;
        }

        if (_tablesInBatch.Count * TransportPacket.Size == written)
        {
            return output.AsMemory(0, written);
        }

        // Nothing after them: the tail begins where the batch ends.
        return Gather(output, written, written, _tablesInBatch.Count);
    }

    /// <summary>
    /// The batch as a reader that has not started yet is given it: the tables that came before
    /// the picture, and everything from the picture on. What lies between them is the tail of a
    /// picture whose beginning that reader never saw - which is exactly what a decoder cannot
    /// use, and says so about for as long as it lasts.
    /// </summary>
    private ReadOnlyMemory<byte> StartFrom(byte[] output, int randomAccess, int written)
    {
        var before = 0;

        while (before < _tablesInBatch.Count && _tablesInBatch[before] < randomAccess)
        {
            before++;
        }

        return before == 0
            ? output.AsMemory(randomAccess, written - randomAccess)
            : Gather(output, randomAccess, written, before);
    }

    /// <summary>
    /// The first <paramref name="tables"/> table packets of the batch, followed by everything
    /// from <paramref name="from"/> on.
    /// </summary>
    private byte[] Gather(byte[] output, int from, int written, int tables)
    {
        var gathered = new byte[(tables * TransportPacket.Size) + written - from];
        var at = 0;

        for (var index = 0; index < tables; index++)
        {
            output.AsSpan(_tablesInBatch[index], TransportPacket.Size).CopyTo(gathered.AsSpan(at));
            at += TransportPacket.Size;
        }

        output.AsSpan(from, written - from).CopyTo(gathered.AsSpan(at));

        return gathered;
    }

    /// <summary>Says now and then how many packets were left out for want of a key, and not more often.</summary>
    private void ReportStillScrambled()
    {
        var count = _stillScrambled - _stillScrambledReported;
        if (count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastScrambledReport < DropWarningInterval)
        {
            return;
        }

        _lastScrambledReport = now;
        _stillScrambledReported = _stillScrambled;
        LogStillScrambled(Channel.Name, count);
    }

    /// <summary>Ends the feed for every reader with <paramref name="error"/>.</summary>
    public void Fail(Exception error)
    {
        ServiceReader[] readers;

        lock (_gate)
        {
            if (_fault is not null)
            {
                return;
            }

            _fault = error;
            _wanted = [];
            readers = [.. _readers];
            _readers.Clear();
            _waitingReaders = 0;
        }

        _mapArrived.TrySetException(error);
        _ready.TrySetException(error);

        foreach (var reader in readers)
        {
            reader.Complete(error);
        }

        LogFailed(Channel.Name, error.Message);
        _pidsChanged();
    }

    public async ValueTask DisposeAsync()
    {
        ServiceReader[] readers;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            readers = [.. _readers];
            _readers.Clear();
            _waitingReaders = 0;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        _mapArrived.TrySetCanceled();
        _ready.TrySetCanceled();

        foreach (var reader in readers)
        {
            reader.Complete();
        }

        if (_opening is not null)
        {
            await _opening.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        if (_descrambler is { } descrambler)
        {
            descrambler.RequiredPidsChanged -= OnRequiredPidsChanged;

            if (descrambler is IServiceDescramblerDiagnostics diagnostics)
            {
                diagnostics.StatusChanged -= OnDescramblingStatusChanged;
            }

            _descrambler = null;
            await descrambler.DisposeAsync().ConfigureAwait(false);
        }

        if (_slot is { } slot)
        {
            _slots.Return(slot);
            _slot = null;
        }

        _lifetime.Dispose();
    }

    private void Unsubscribe(ServiceReader reader)
    {
        lock (_gate)
        {
            if (_readers.Remove(reader) && reader.WaitsForPicture)
            {
                reader.WaitsForPicture = false;
                _waitingReaders--;
            }
        }
    }

    /// <summary>
    /// Hands a batch to every reader. <paramref name="cleanStart"/> is the same batch as a reader
    /// that has not started yet is given it - from the first picture it can start on - or null
    /// where this batch carries no such picture.
    /// </summary>
    private void Dispatch(ReadOnlyMemory<byte> data, ReadOnlyMemory<byte>? cleanStart = null, ReadOnlyMemory<byte> tables = default)
    {
        long dropped = 0;
        var startedWithoutAPicture = false;
        var now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            foreach (var reader in _readers)
            {
                if (reader.WaitsForPicture)
                {
                    if (cleanStart is { } start)
                    {
                        Started(reader);
                        reader.Offer(start);
                    }
                    else if (now >= reader.PictureWaitUntil)
                    {
                        // No picture said it stood on its own for as long as anybody can be kept
                        // waiting. Better a decoder that complains for a moment than a viewer
                        // looking at nothing.
                        Started(reader);
                        startedWithoutAPicture = true;
                        reader.Offer(data);
                    }
                    else if (!tables.IsEmpty)
                    {
                        reader.Offer(tables);
                    }

                    continue;
                }

                reader.Offer(data);
            }

            if (now - _lastDropWarning >= DropWarningInterval)
            {
                foreach (var reader in _readers)
                {
                    dropped += reader.TakeDropped();
                }

                _lastDropWarning = now;
            }
        }

        if (startedWithoutAPicture)
        {
            LogNoCleanStart(Channel.Name, (long)_timings.CleanStart.TotalMilliseconds);
        }

        if (dropped > 0)
        {
            LogReaderTooSlow(Channel.Name, dropped);
        }
    }

    /// <summary>A reader that waited for a picture and has one. Under <see cref="_gate"/>.</summary>
    private void Started(ServiceReader reader)
    {
        reader.WaitsForPicture = false;
        _waitingReaders--;
    }

    private void OnPatSection(ReadOnlySpan<byte> section)
    {
        if (ProgramAssociationTable.Parse(section) is not { } pat)
        {
            return;
        }

        if (pat.TransportStreamId != Channel.TransportStreamId && !pat.Programs.ContainsKey(_serviceId))
        {
            // Another transport stream's PAT without the service: left over from what the tuner
            // was tuned to before, as a Kathrein EXIP-418 sends after a switch. This transponder's
            // own PAT follows; a service that is not in that one fails there.
            return;
        }

        _transportStreamId = pat.TransportStreamId;

        if (!pat.Programs.TryGetValue(_serviceId, out var pmtPid))
        {
            if (pat.LastSectionNumber == 0)
            {
                Fail(DvbErrors.ServiceMissing(Channel.Name, _serviceId, _transponder));
            }

            return;
        }

        if (pmtPid == _pmtPid)
        {
            return;
        }

        LogProgramMapMoved(Channel.Name, _pmtPid, pmtPid);

        _pmtPid = pmtPid;
        _pmtAssembler = new SectionAssembler();
        _lastPmtContinuity = -1;
        _patVersion = (_patVersion + 1) & 0x1F;
        _patPacket = BuildPatPacket();

        UpdateWanted();
    }

    private void OnPmtSection(ReadOnlySpan<byte> section)
    {
        if (ProgramMapTable.Parse(section) is not { } programMap || programMap.ProgramNumber != _serviceId)
        {
            return;
        }

        var previous = _programMap;
        if (previous is not null && previous.Version == programMap.Version && previous.Section.AsSpan().SequenceEqual(programMap.Section))
        {
            return;
        }

        _programMap = programMap;
        _outputPids = [.. programMap.PlayablePids];
        _ecmPids = [.. programMap.EcmPids];
        _videoPid = programMap.Streams.FirstOrDefault(stream => stream.Kind == StreamKind.Video)?.Pid ?? -1;
        _gatedPids = programMap.Streams.Where(stream => stream.Kind is StreamKind.Video or StreamKind.Audio).Select(stream => stream.Pid).ToHashSet();
        UpdateWanted();

        if (previous is null)
        {
            LogProgramMap(Channel.Name, programMap.Streams.Count, programMap.HasConditionalAccess);
            Interlocked.CompareExchange(ref _mapAt, Stopwatch.GetTimestamp(), 0);
            _mapArrived.TrySetResult();
        }

        if (programMap.HasConditionalAccess || _scrambled)
        {
            if (_descrambler is { } descrambler && previous is not null)
            {
                _ = UpdateDescramblerAsync(descrambler, programMap);
            }
            else
            {
                StartDescrambling(programMap);
            }
        }
        else
        {
            MarkReady();
        }
    }

    /// <summary>Opens a descrambler for this service, or fails the feed with the reason it cannot.</summary>
    private void StartDescrambling(ProgramMapTable programMap)
    {
        _scrambled = true;

        if (_opening is not null)
        {
            return;
        }

        if (!_descramblers.IsConfigured)
        {
            Fail(DvbErrors.DescramblingNotConfigured(Channel.Name));
            return;
        }

        var slot = _slots.Rent();
        _slot = slot;

        var request = new ServiceDescramblingRequest
        {
            ServiceId = _serviceId,
            ProgramMapSection = programMap.Section,
            ProgramMapPid = _pmtPid,
            Slot = slot,
            TransportStreamId = _transportStreamId,
            OriginalNetworkId = Channel.OriginalNetworkId,
            ChannelName = Channel.Name,
            RecentEcms = _recentEcms,
        };

        _opening = Task.Run(() => OpenDescramblerAsync(request), CancellationToken.None);
    }

    private async Task OpenDescramblerAsync(ServiceDescramblingRequest request)
    {
        IServiceDescrambler descrambler;

        try
        {
            descrambler = await _descramblers.OpenAsync(request, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (TunerException error)
        {
            Fail(error);
            return;
        }
        catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or TimeoutException
            or InvalidOperationException)
        {
            Fail(DvbErrors.DescramblerFailed(Channel.Name, error.Message));
            return;
        }

        if (_lifetime.IsCancellationRequested)
        {
            await descrambler.DisposeAsync().ConfigureAwait(false);
            return;
        }

        descrambler.RequiredPidsChanged += OnRequiredPidsChanged;

        if (descrambler is IServiceDescramblerDiagnostics diagnostics)
        {
            diagnostics.StatusChanged += OnDescramblingStatusChanged;
        }

        _descrambler = descrambler;
        UpdateWanted();
        LogDescramblerOpened(Channel.Name, request.Slot, new PidList(descrambler.RequiredPids));

        // A program map that changed while the descrambler was being opened.
        if (_programMap is { } current && !current.Section.AsSpan().SequenceEqual(request.ProgramMapSection.Span))
        {
            await UpdateDescramblerAsync(descrambler, current).ConfigureAwait(false);
        }

        // Ready once the card has answered: a player given the stream before that would see
        // nothing, and decide from nothing.
        var waited = Stopwatch.StartNew();

        try
        {
            while (!descrambler.HasKeys)
            {
                if (waited.Elapsed >= _timings.Keys)
                {
                    // Where the descrambler knows why - the server unreachable, the card
                    // refusing the ECM - that is the better error than "no key".
                    Fail(descrambler is IServiceDescramblerDiagnostics { Status.Problem: { } problem }
                        ? problem
                        : DvbErrors.NoKeys(Channel.Name, _timings.Keys));
                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50), _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _hasKeys = true;

        // The ECM pids nobody filters for are not needed from the tuner any more.
        UpdateWanted();

        var leftOut = Interlocked.Read(ref _beforeKeys);
        LogFirstKey(Channel.Name, (long)waited.Elapsed.TotalMilliseconds, leftOut);

        MarkReady();
    }

    private void MarkReady()
    {
        Interlocked.CompareExchange(ref _readyAt, Stopwatch.GetTimestamp(), 0);
        _ready.TrySetResult();
    }

    private async Task UpdateDescramblerAsync(IServiceDescrambler descrambler, ProgramMapTable programMap)
    {
        try
        {
            await descrambler.UpdateProgramMapAsync(programMap.Section, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is TunerException or IOException or OperationCanceledException
            or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            LogProgramMapUpdateFailed(Channel.Name, error.Message);
        }
    }

    private void OnRequiredPidsChanged(object? sender, EventArgs e) => UpdateWanted();

    private void KeepRecentEcms(ReadOnlySpan<byte> packets)
    {
        var ecmPids = _ecmPids;
        if (ecmPids.Count == 0)
        {
            return;
        }

        for (var offset = 0; offset + TransportPacket.Size <= packets.Length; offset += TransportPacket.Size)
        {
            var packet = packets.Slice(offset, TransportPacket.Size);
            var pid = TransportPacket.Pid(packet);
            if (ecmPids.Contains(pid))
            {
                _recentEcms.Keep(pid, packet);
            }
        }
    }

    /// <summary>
    /// Keeps and logs what the descrambler says is wrong, and that it is fine again. Raised from
    /// the descrambler's own threads; nothing here waits.
    /// </summary>
    private void OnDescramblingStatusChanged(object? sender, EventArgs e)
    {
        if (sender is not IServiceDescramblerDiagnostics diagnostics)
        {
            return;
        }

        var problem = diagnostics.Status.Problem;
        var previous = _descramblingProblem;
        _descramblingProblem = problem;

        if (problem is not null && (previous is null || previous.Code != problem.Code || previous.Message != problem.Message))
        {
            LogDescramblingProblem(Channel.Name, problem.Code, problem.Message);
        }
        else if (problem is null && previous is not null)
        {
            LogDescramblingRecovered(Channel.Name);
        }
    }

    private void UpdateWanted()
    {
        lock (_gate)
        {
            if (_fault is not null || _disposed)
            {
                return;
            }

            _wanted = ComputeWanted();
        }

        _pidsChanged();
    }

    private HashSet<int> ComputeWanted()
    {
        var wanted = new HashSet<int>(_outputPids) { TransportPacket.PatPid };

        if (_pmtPid > 0)
        {
            wanted.Add(_pmtPid);
        }

        // The ECMs from the moment the tuner is asked, rather than from the moment the server
        // asks for them: that would be another round to the tuner after the CA PMT, and another
        // wait for the broadcaster to send the next ECM after that. Once the first key is here,
        // only the ones the server filters for are still asked for.
        if (!_hasKeys)
        {
            wanted.UnionWith(_ecmPids);
        }

        if (_descrambler is { } descrambler)
        {
            // ECMs, and the CAT and EMMs the server asks for: whatever it asks for, it gets.
            wanted.UnionWith(descrambler.RequiredPids);
        }

        return wanted;
    }

    private byte[] BuildPatPacket()
    {
        var section = SectionPacketizer.ProgramAssociation(_transportStreamId, _patVersion, _serviceId, Math.Max(_pmtPid, 0));
        var packets = new List<byte[]>(1);
        SectionPacketizer.Packetize(section, TransportPacket.PatPid, 0, packets);

        return packets[0];
    }

    [LoggerMessage(EventId = 9830, Level = LogLevel.Information, Message = "Program map of '{Channel}': {StreamCount} streams, conditional access {Scrambled}.")]
    private partial void LogProgramMap(string channel, int streamCount, bool scrambled);

    [LoggerMessage(EventId = 9831, Level = LogLevel.Information, Message = "The program map of '{Channel}' moved from pid {OldPid} to pid {NewPid}.")]
    private partial void LogProgramMapMoved(string channel, int oldPid, int newPid);

    [LoggerMessage(EventId = 9832, Level = LogLevel.Warning, Message = "'{Channel}' has scrambled packets on pid {Pid} although its program map names no conditional access system.")]
    private partial void LogScramblingBits(string channel, int pid);

    [LoggerMessage(EventId = 9833, Level = LogLevel.Information, Message = "Descrambling '{Channel}' in slot {Slot}; the conditional access server asked for pids {Pids}.")]
    private partial void LogDescramblerOpened(string channel, int slot, PidList pids);

    [LoggerMessage(EventId = 9834, Level = LogLevel.Warning, Message = "The new program map of '{Channel}' could not be passed to the conditional access server: {Reason}")]
    private partial void LogProgramMapUpdateFailed(string channel, string reason);

    [LoggerMessage(EventId = 9835, Level = LogLevel.Warning, Message = "A reader of '{Channel}' fell behind; {Bytes} bytes were dropped for it rather than holding up the shared tuner.")]
    private partial void LogReaderTooSlow(string channel, long bytes);

    [LoggerMessage(EventId = 9837, Level = LogLevel.Warning, Message = "Descrambling '{Channel}' has a problem ({Code}): {Reason}")]
    private partial void LogDescramblingProblem(string channel, string code, string reason);

    [LoggerMessage(EventId = 9838, Level = LogLevel.Information, Message = "Descrambling '{Channel}' works again.")]
    private partial void LogDescramblingRecovered(string channel);

    [LoggerMessage(EventId = 9839, Level = LogLevel.Information, Message = "{Count} packets of '{Channel}' were still scrambled and left out: no key for their parity yet.")]
    private partial void LogStillScrambled(string channel, long count);

    [LoggerMessage(EventId = 9885, Level = LogLevel.Warning, Message = "'{Channel}' has scrambled packets on pid {Pid} and nothing to descramble them with: {State}.")]
    private partial void LogKeyMissing(string channel, int pid, string state);

    [LoggerMessage(EventId = 9840, Level = LogLevel.Information, Message = "The first key for '{Channel}' arrived after {Milliseconds} ms; the {Count} packets from before it were left out.")]
    private partial void LogFirstKey(string channel, long milliseconds, long count);

    [LoggerMessage(EventId = 9847, Level = LogLevel.Information, Message = "The key for '{Channel}' came {Milliseconds} ms after the parity changed; {Count} packets waited for it and went on whole.")]
    private partial void LogKeyLate(string channel, long milliseconds, long count);

    [LoggerMessage(EventId = 9848, Level = LogLevel.Warning, Message = "No key fitted '{Channel}' within {Milliseconds} ms of the parity changing; {Count} packets that waited are left out, and the picture holds until one fits.")]
    private partial void LogKeyTooLate(string channel, long milliseconds, long count);

    [LoggerMessage(EventId = 9841, Level = LogLevel.Information, Message = "No picture of '{Channel}' that a player could start on arrived within {Milliseconds} ms; a viewer was handed the stream as it is.")]
    private partial void LogNoCleanStart(string channel, long milliseconds);

    [LoggerMessage(EventId = 9836, Level = LogLevel.Warning, Message = "'{Channel}' stopped: {Reason}")]
    private partial void LogFailed(string channel, string reason);
}
