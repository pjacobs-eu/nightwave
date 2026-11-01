using System.Globalization;
using System.Security.Cryptography;
using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb.Descrambling.Csa;
using Microsoft.Extensions.Logging;

namespace Nightwave.Tuners.Dvb.Descrambling;

/// <summary>
/// One service descrambled with control words from the descrambling server: forwards the sections
/// the server filters for, keeps the keys it sends, and descrambles the packets in place.
/// </summary>
/// <remarks>
/// <para>
/// Two threads meet here. The server's messages arrive on the connection's reader (filters, pids,
/// keys); <see cref="Process"/> runs on the tuner's stream loop. Everything the reader changes is
/// published as a new immutable object - the filter set, a descrambler index's key pair - or a
/// single int, so <see cref="Process"/> reads without locking and sees either the old or the new
/// state, never half of one. The reader side serialises its own updates with a lock.
/// </para>
/// <para>
/// Keys and packets: a packet whose scrambling control bits say even (10) is descrambled with the
/// even key of the descrambler index its pid is mapped to, odd (11) with the odd key. A new key
/// only ever replaces the key of its own parity, so packets still flagged with the other parity
/// keep using the key they were scrambled with - which is how the broadcaster's key change is
/// meant to work: the next key arrives while the current one is still in use. Keys are looked up
/// per packet; a key that arrives while a call runs applies from the next packet of its parity.
/// </para>
/// </remarks>
internal sealed partial class DvbApiServiceDescrambler : IServiceDescrambler, IServiceDescramblerDiagnostics
{
    private const int PidCount = 8192;
    private const int MaxIndexes = 64;
    private const int PacketSize = 188;

    private readonly DvbApiClient _client;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly object _stateLock = new();
    private readonly object _statusLock = new();
    private readonly CsaBatchDescrambler _csa = new();

    // Published by the reader, read by Process.
    private FilterSet _filters = FilterSet.Empty;
    private readonly int[] _pidToIndex = new int[PidCount]; // index + 1; 0 not told, -1 switched off
    private readonly KeyPair?[] _keys = new KeyPair?[MaxIndexes];
    private int _lastKeyedIndex = -1;

    // Written by Process, read by the reader: the iCAM mode of the last ECM of each parity, and
    // of the last ECM at all; -1 before the first.
    private readonly int[] _ecmModeByParity = [-1, -1];
    private int _lastEcmMode = -1;

    // Reader side only, under _stateLock.
    private readonly IndexState?[] _indexStates = new IndexState?[MaxIndexes];

    // Process side only.
    private readonly Dictionary<int, DemuxSectionAssembler> _assemblers = [];
    private FilterSet _assemblersFor = FilterSet.Empty;

    // Noted from Process, and from the reader when it hands over a kept ECM; under its own lock.
    private readonly Dictionary<int, int> _ecmModeByPid = [];
    private readonly Lock _ecmModeLock = new();

    // When each key parity last arrived, and when an ECM of each table id (0x80, 0x81) was last
    // seen on the air and last handed to the server; timestamps, 0 until it happened.
    private readonly long[] _keyAt = new long[2];
    private readonly long[] _ecmSeenAt = new long[2];
    private readonly long[] _ecmSentAt = new long[2];
    private readonly long[] _pidMappedAt = new long[PidCount];

    // How long the first key took, and where the time went; timestamps, 0 until it happened.
    private long _watchingSince;
    private long _firstEcmFilterAt;
    private long _firstEcmSentAt;
    private readonly List<Batch> _batches = [];

    private CaProgramMap _programMap;
    private ServiceDescramblingRequest _request;
    private ITimer? _controlWordTimer;
    private DescramblingStatus _status;
    private long _ecmSections;
    private long _otherSections;
    private long _emmSections;
    private bool _unsupportedIcamReported;
    private int _ecmFilterSeen;
    private int _disposed;

    public DvbApiServiceDescrambler(
        ServiceDescramblingRequest request,
        CaProgramMap programMap,
        DvbApiClient client,
        ILogger logger,
        TimeProvider time)
    {
        _request = request;
        _programMap = programMap;
        _client = client;
        _logger = logger;
        _time = time;
        _status = new DescramblingStatus { Server = client.Endpoint };
    }

    public int Slot => _request.Slot;

    public string ChannelName => _request.ChannelName;

    public IReadOnlyCollection<int> RequiredPids => Volatile.Read(ref _filters).Pids;

    public event EventHandler? RequiredPidsChanged;

    public event EventHandler? StatusChanged;

    public bool HasKeys => Volatile.Read(ref _lastKeyedIndex) >= 0;

    public DescramblingStatus Status
    {
        get
        {
            var status = Volatile.Read(ref _status);
            return status with
            {
                Connected = _client.IsConnected,
                ServerVersion = _client.ServerVersion,
                HasKeys = HasKeys,
                EcmSectionsForwarded = Interlocked.Read(ref _ecmSections),
                OtherSectionsForwarded = Interlocked.Read(ref _otherSections),
                EmmSectionsForwarded = Interlocked.Read(ref _emmSections),
                SectionsDropped = _client.Dropped,
            };
        }
    }

    /// <summary>The CA PMT the server gets for this service.</summary>
    public byte[] BuildCaPmt(byte listManagement)
    {
        var request = Volatile.Read(ref _request);
        return Volatile.Read(ref _programMap).BuildCaPmt(
            listManagement,
            request.Slot,
            request.ProgramMapPid,
            request.TransportStreamId,
            request.OriginalNetworkId);
    }

    /// <summary>Starts the wait for the first control word; called once the server has the CA PMT.</summary>
    public void StartWatching()
    {
        Interlocked.CompareExchange(ref _watchingSince, _time.GetTimestamp(), 0);
        RestartControlWordTimer();
    }

    // -------------------------------------------------------------------------------------------
    // The stream
    // -------------------------------------------------------------------------------------------

    public void Process(Span<byte> packets)
    {
        ForwardSections(packets);
        Descramble(packets);
    }

    public void ForwardSections(ReadOnlySpan<byte> packets)
    {
        var filters = Volatile.Read(ref _filters);
        if (!ReferenceEquals(filters, _assemblersFor))
        {
            PruneAssemblers(filters);
        }

        for (var offset = 0; offset + PacketSize <= packets.Length; offset += PacketSize)
        {
            var packet = packets.Slice(offset, PacketSize);
            if (packet[0] != 0x47)
            {
                continue;
            }

            var pid = ((packet[1] & 0x1F) << 8) | packet[2];
            if (filters.Wants(pid))
            {
                ForwardSections(pid, packet, filters);
            }
        }
    }

    public KeyProbe Probe(ReadOnlySpan<byte> packet)
    {
        var scrambling = packet[3] >> 6;
        var pid = ((packet[1] & 0x1F) << 8) | packet[2];

        if (packet[0] != 0x47 || scrambling < 2 || Volatile.Read(ref _pidToIndex[pid]) < 0)
        {
            return KeyProbe.NotApplicable;
        }

        var payloadStart = ((packet[3] >> 4) & 0x3) switch
        {
            0x1 => 4,
            0x3 => 5 + packet[4],
            _ => PacketSize,
        };

        // One block of any of the ciphers: what is shorter is left in the clear by all of them.
        if (payloadStart > PacketSize - 16)
        {
            return KeyProbe.NotApplicable;
        }

        if (KeyFor(pid, scrambling == 3) is not { } key)
        {
            return KeyProbe.Wrong;
        }

        Span<byte> copy = stackalloc byte[PacketSize];
        packet.CopyTo(copy);

        try
        {
            key.Descramble(_csa, copy, [new CsaPayload(payloadStart, PacketSize - payloadStart)]);
        }
        catch (CryptographicException)
        {
            return KeyProbe.Wrong;
        }

        return copy[payloadStart] == 0 && copy[payloadStart + 1] == 0 && copy[payloadStart + 2] == 1 ? KeyProbe.Fits : KeyProbe.Wrong;
    }

    public void Descramble(Span<byte> packets)
    {
        var usedBatches = 0;

        for (var offset = 0; offset + PacketSize <= packets.Length; offset += PacketSize)
        {
            var packet = packets.Slice(offset, PacketSize);
            if (packet[0] != 0x47)
            {
                continue;
            }

            var pid = ((packet[1] & 0x1F) << 8) | packet[2];
            var scrambling = packet[3] >> 6;
            if (scrambling < 2)
            {
                continue;
            }

            var key = KeyFor(pid, scrambling == 3);
            if (key is null)
            {
                continue; // no key yet: passed on scrambled, as the contract says
            }

            var adaptation = (packet[3] >> 4) & 0x3;
            var payloadStart = adaptation switch
            {
                0x1 => 4,
                0x3 => 5 + packet[4],
                _ => PacketSize, // no payload
            };

            if (payloadStart > PacketSize)
            {
                continue; // an adaptation field longer than the packet: broken, left as it is
            }

            if (payloadStart < PacketSize)
            {
                usedBatches = AddToBatch(key, offset, offset + payloadStart, PacketSize - payloadStart, usedBatches);
            }
            else
            {
                packet[3] &= 0x3F;
            }
        }

        for (var i = 0; i < usedBatches; i++)
        {
            RunBatch(_batches[i], packets);
        }
    }

    private ContentKey? KeyFor(int pid, bool odd)
    {
        var mapped = Volatile.Read(ref _pidToIndex[pid]);
        if (mapped < 0)
        {
            return null; // the descrambling server switched this pid off
        }

        var index = mapped - 1;
        if (index < 0)
        {
            // Streams the server was not told about (a PMT without them) are tried with the
            // key that came last; with one ECM stream that is the right one.
            index = Volatile.Read(ref _lastKeyedIndex);
            if (index < 0)
            {
                return null;
            }
        }

        var pair = Volatile.Read(ref _keys[index]);
        return odd ? pair?.Odd : pair?.Even;
    }

    private int AddToBatch(ContentKey key, int packetOffset, int payloadOffset, int payloadLength, int usedBatches)
    {
        for (var i = 0; i < usedBatches; i++)
        {
            if (ReferenceEquals(_batches[i].Key, key))
            {
                _batches[i].Add(packetOffset, payloadOffset, payloadLength);
                return usedBatches;
            }
        }

        if (usedBatches == _batches.Count)
        {
            _batches.Add(new Batch());
        }

        var batch = _batches[usedBatches];
        batch.Reset(key);
        batch.Add(packetOffset, payloadOffset, payloadLength);
        return usedBatches + 1;
    }

    private void RunBatch(Batch batch, Span<byte> packets)
    {
        try
        {
            batch.Key.Descramble(_csa, packets, batch.Payloads);
        }
        catch (CryptographicException exception)
        {
            // A key .NET refuses (DES calls some keys weak). The packets stay as they are.
            ReportProblem(DescramblerErrors.UnsupportedAlgorithm(ChannelName, $"{batch.Key.Algorithm} with this key ({exception.Message})"));
            return;
        }

        foreach (var packetOffset in batch.PacketOffsets)
        {
            packets[packetOffset + 3] &= 0x3F;
        }
    }

    private void ForwardSections(int pid, ReadOnlySpan<byte> packet, FilterSet filters)
    {
        if (!_assemblers.TryGetValue(pid, out var assembler))
        {
            assembler = new DemuxSectionAssembler();
            _assemblers[pid] = assembler;
        }

        assembler.Feed(packet, (Service: this, Pid: pid, Filters: filters), static (section, state) =>
            state.Service.OnSection(state.Pid, section, state.Filters));
    }

    /// <summary>
    /// Hands a new filter the ECM the tuner kept for its pid, if it is one the filter takes. The
    /// tuner is asked for the ECM pids with the program map, so by the time the server has the
    /// CA PMT and sets its filter there is usually one kept already.
    /// </summary>
    private void SendKeptEcm(DemuxFilter filter)
    {
        if (_request.RecentEcms is not { } recent
            || !recent.TryTake(filter.Pid, out var ecm)
            || !filter.Matches(ecm))
        {
            return;
        }

        Forward(filter, ecm);
        NoteEcmSent(filter.Pid, IcamEcm.ParityOf(ecm), ecm);
    }

    private void OnSection(int pid, ReadOnlySpan<byte> section, FilterSet filters)
    {
        var parity = IcamEcm.ParityOf(section);
        var isEcm = parity >= 0 && Volatile.Read(ref _programMap).EcmPids.ContainsKey(pid);
        if (isEcm)
        {
            Volatile.Write(ref _ecmSeenAt[parity], _time.GetTimestamp());
        }

        var forwarded = false;
        foreach (var filter in filters.OnPid(pid))
        {
            if (filter.Matches(section))
            {
                Forward(filter, section);
                forwarded = true;
            }
        }

        if (!forwarded)
        {
            return;
        }

        if (isEcm)
        {
            NoteEcmSent(pid, parity, section);
        }
        else
        {
            Interlocked.Increment(ref _otherSections);

            // EMMs carry table ids 0x82 to 0x8F.
            if (section.Length > 0 && section[0] is >= 0x82 and <= 0x8F)
            {
                Interlocked.Increment(ref _emmSections);
            }
        }
    }

    private void Forward(DemuxFilter filter, ReadOnlySpan<byte> section)
    {
        _client.SendFilterData(filter.Demux, filter.Filter, section);
        if (filter.IsOneShot)
        {
            RemoveFilter(filter.Demux, filter.Filter);
        }
    }

    private void NoteEcmSent(int pid, int parity, ReadOnlySpan<byte> section)
    {
        Volatile.Write(ref _ecmSentAt[parity], _time.GetTimestamp());
        Interlocked.CompareExchange(ref _firstEcmSentAt, _time.GetTimestamp(), 0);
        Interlocked.Increment(ref _ecmSections);
        NoteEcmMode(pid, section);
    }

    /// <summary>Remembers the iCAM mode of this ECM for the control word it will produce.</summary>
    private void NoteEcmMode(int pid, ReadOnlySpan<byte> section)
    {
        var caid = 0;
        byte mode = 0;
        foreach (var candidate in Volatile.Read(ref _programMap).EcmPids[pid])
        {
            var candidateMode = IcamEcm.ModeOf(section, candidate);
            if (candidateMode != 0 || caid == 0)
            {
                caid = candidate;
                mode = candidateMode;
            }
        }

        var parity = IcamEcm.ParityOf(section);
        Volatile.Write(ref _ecmModeByParity[parity], mode);
        Volatile.Write(ref _lastEcmMode, mode);

        bool changed;
        lock (_ecmModeLock)
        {
            changed = !_ecmModeByPid.TryGetValue(pid, out var previous) || previous != mode;
            _ecmModeByPid[pid] = mode;
        }

        if (changed)
        {
            if (mode != 0)
            {
                LogIcam(ChannelName, caid, pid, mode);
                if (mode != CsaKey.IcamMode && !_unsupportedIcamReported)
                {
                    _unsupportedIcamReported = true;
                    ReportProblem(DescramblerErrors.UnsupportedIcamMode(ChannelName, caid, mode));
                }
            }
        }
    }

    private void PruneAssemblers(FilterSet filters)
    {
        foreach (var pid in _assemblers.Keys.ToList())
        {
            if (!filters.Wants(pid))
            {
                _assemblers.Remove(pid);
            }
        }

        _assemblersFor = filters;
    }

    public Task UpdateProgramMapAsync(ReadOnlyMemory<byte> programMapSection, CancellationToken cancellationToken)
    {
        CaProgramMap map;
        try
        {
            map = CaProgramMap.Parse(programMapSection.Span);
        }
        catch (FormatException exception)
        {
            throw DescramblerErrors.InvalidProgramMap(ChannelName, exception.Message);
        }

        Volatile.Write(ref _programMap, map);
        Volatile.Write(ref _request, _request with { ProgramMapSection = programMapSection });
        _client.UpdateService(this);
        LogProgramMapUpdated(ChannelName, map.Version);
        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------------------------
    // The server's messages, on the connection's reader
    // -------------------------------------------------------------------------------------------

    public void OnMessage(DvbApiServerMessage message)
    {
        switch (message)
        {
            case DvbApiSetFilter set:
                SetFilter(set);
                break;
            case DvbApiStopFilter stop:
                RemoveFilter(stop.Demux, stop.Filter);
                break;
            case DvbApiSetPid setPid:
                SetPid(setPid);
                break;
            case DvbApiSetDescramblerMode mode:
                SetMode(mode);
                break;
            case DvbApiSetDescrambler descrambler:
                SetControlWord(descrambler);
                break;
            case DvbApiSetDescramblerData data:
                SetData(data);
                break;
            case DvbApiEcmInfo info:
                OnEcmInfo(info);
                break;
        }
    }

    private void SetFilter(DvbApiSetFilter set)
    {
        var filter = new DemuxFilter(set.Demux, set.Filter, set.Pid, set.Data, set.Mask, set.Mode, set.Flags);
        var isEcmPid = Volatile.Read(ref _programMap).EcmPids.ContainsKey(set.Pid);
        if (isEcmPid)
        {
            Volatile.Write(ref _ecmFilterSeen, 1);
            Interlocked.CompareExchange(ref _firstEcmFilterAt, _time.GetTimestamp(), 0);
        }

        bool pidsChanged;
        lock (_stateLock)
        {
            var current = _filters;
            var updated = current.With(filter);
            pidsChanged = !current.Wants(set.Pid);
            Volatile.Write(ref _filters, updated);
        }

        LogFilterSet(ChannelName, set.Demux, set.Filter, set.Pid, set.Data[0], set.Mask[0]);
        if (pidsChanged)
        {
            RequiredPidsChanged?.Invoke(this, EventArgs.Empty);
        }

        if (isEcmPid)
        {
            SendKeptEcm(filter);
        }
    }

    private void RemoveFilter(int demux, int filter)
    {
        bool pidsChanged;
        lock (_stateLock)
        {
            var current = _filters;
            var updated = current.Without(demux, filter);
            if (ReferenceEquals(updated, current))
            {
                return;
            }

            pidsChanged = updated.Pids.Count != current.Pids.Count;
            Volatile.Write(ref _filters, updated);
        }

        LogFilterStopped(ChannelName, demux, filter);
        if (pidsChanged)
        {
            RequiredPidsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetPid(DvbApiSetPid setPid)
    {
        // With extended_cw_api = 2 the server flags DES by adding 0x100 to the index.
        var index = setPid.Index < 0 ? -1 : setPid.Index & 0xFF;
        if (index >= MaxIndexes)
        {
            LogIndexOutOfRange(ChannelName, setPid.Index);
            return;
        }

        lock (_stateLock)
        {
            if (index >= 0 && (setPid.Index & 0x100) != 0)
            {
                var state = StateOf(index);
                state.Algorithm = DvbApiSetDescramblerMode.AlgorithmDes;
                state.CipherMode = DvbApiSetDescramblerMode.ModeEcb;
            }

            // A pid that is switched off (-1) is passed on as it is from now on.
            Volatile.Write(ref _pidToIndex[setPid.Pid], index < 0 ? -1 : index + 1);
            Volatile.Write(ref _pidMappedAt[setPid.Pid], _time.GetTimestamp());
        }

        LogPidMapped(ChannelName, setPid.Pid, setPid.Index);
    }

    private void SetMode(DvbApiSetDescramblerMode mode)
    {
        if (mode.Index is < 0 or >= MaxIndexes)
        {
            LogIndexOutOfRange(ChannelName, mode.Index);
            return;
        }

        var supported = mode.Algorithm switch
        {
            DvbApiSetDescramblerMode.AlgorithmCsa or DvbApiSetDescramblerMode.AlgorithmCsaAlt => true,
            DvbApiSetDescramblerMode.AlgorithmAes128 => mode.CipherMode is DvbApiSetDescramblerMode.ModeEcb or DvbApiSetDescramblerMode.ModeCbc,
            DvbApiSetDescramblerMode.AlgorithmDes => mode.CipherMode == DvbApiSetDescramblerMode.ModeEcb,
            _ => false,
        };

        if (!supported)
        {
            var name = mode.Algorithm switch
            {
                DvbApiSetDescramblerMode.AlgorithmCsa => "DVB-CSA",
                DvbApiSetDescramblerMode.AlgorithmDes => "DES",
                DvbApiSetDescramblerMode.AlgorithmAes128 => "AES-128",
                _ => string.Create(CultureInfo.InvariantCulture, $"algorithm {mode.Algorithm}"),
            };
            var cipherMode = mode.CipherMode == DvbApiSetDescramblerMode.ModeCbc ? "CBC" : "ECB";
            ReportProblem(DescramblerErrors.UnsupportedAlgorithm(ChannelName, $"{name} ({cipherMode})"));
        }

        // For the control words that come after it, not for the ones already here. The server
        // sends a mode ahead of every control word, and the cipher mode in it is not steady: a
        // CSA answer from a reader comes with CBC, one from the cache without. Clearing both keys
        // of the index on every change threw away the key of the other parity each time a new one
        // arrived - the one the broadcaster switches to next - and every crypto period change left
        // the picture scrambled until the period after brought that key again: ten seconds of a
        // frozen picture, every other change, and a channel that opened on nothing.
        lock (_stateLock)
        {
            var state = StateOf(mode.Index);
            state.Algorithm = mode.Algorithm;
            state.CipherMode = mode.CipherMode;
        }
    }

    private void SetControlWord(DvbApiSetDescrambler descrambler)
    {
        if (descrambler.Index is < 0 or >= MaxIndexes || descrambler.Parity is < 0 or > 1)
        {
            LogIndexOutOfRange(ChannelName, descrambler.Index);
            return;
        }

        var parity = descrambler.Parity;
        var byParity = Volatile.Read(ref _ecmModeByParity[parity]);
        var ecmMode = byParity >= 0 ? byParity : Math.Max(0, Volatile.Read(ref _lastEcmMode));

        lock (_stateLock)
        {
            var state = StateOf(descrambler.Index);
            state.SetMaterial(parity, descrambler.ControlWord);
            state.EcmMode[parity] = (byte)ecmMode;
            PublishKeys(descrambler.Index, state);
        }

        OnKeyArrived(descrambler.Index, parity);
        ChangeStatus(status => (status with { KeysReceived = status.KeysReceived + 1 }, false));
    }

    private void SetData(DvbApiSetDescramblerData data)
    {
        if (data.Index is < 0 or >= MaxIndexes || data.Parity is < 0 or > 1)
        {
            LogIndexOutOfRange(ChannelName, data.Index);
            return;
        }

        lock (_stateLock)
        {
            var state = StateOf(data.Index);
            if (data.DataType == DvbApiSetDescramblerData.TypeIv)
            {
                state.Iv = data.Data;
            }
            else
            {
                state.SetMaterial(data.Parity, data.Data);
            }

            PublishKeys(data.Index, state);
        }

        if (data.DataType == DvbApiSetDescramblerData.TypeKey)
        {
            OnKeyArrived(data.Index, data.Parity);
        }
    }

    private IndexState StateOf(int index) => _indexStates[index] ??= new IndexState();

    /// <summary>Builds the keys of one index from its material and publishes them. Under the lock.</summary>
    private void PublishKeys(int index, IndexState state)
    {
        // A control word that makes no key - cut short, garbled on the way, of a length its
        // algorithm does not have - leaves the key of its parity as it was. The picture then
        // goes on with the last good key until the next ECM brings a better one, rather than
        // turning into noise the moment one answer was bad. Each parity is built with the algorithm
        // its own control word came with, so a key of the other parity is never rebuilt under an
        // algorithm it was not sent for.
        var previous = Volatile.Read(ref _keys[index]);
        var even = BuildKey(state, 0) ?? (state.Material[0] is null ? null : previous?.Even);
        var odd = BuildKey(state, 1) ?? (state.Material[1] is null ? null : previous?.Odd);
        Volatile.Write(ref _keys[index], new KeyPair(even, odd));
    }

    private ContentKey? BuildKey(IndexState state, int parity)
    {
        var material = state.Material[parity];
        if (material is null)
        {
            return null;
        }

        try
        {
            return state.AlgorithmOf[parity] switch
            {
                DvbApiSetDescramblerMode.AlgorithmCsa when material.Length == 8 =>
                    new CsaContentKey(CsaKey.Create(material, state.EcmMode[parity])),

                // The server has already decided this is iCAM, by the same test IcamEcm makes, so
                // the schedule is the iCAM one whatever this side read from the ECM: the server
                // sends CSA-ALT exactly when its own check matches. Where the two disagree, the
                // server is the one that saw the card's answer.
                DvbApiSetDescramblerMode.AlgorithmCsaAlt when material.Length == 8 =>
                    new CsaContentKey(CsaKey.Create(material, CsaKey.IcamMode)),
                DvbApiSetDescramblerMode.AlgorithmDes when material.Length == 8 =>
                    new DesContentKey(material),
                DvbApiSetDescramblerMode.AlgorithmAes128 when material.Length == 16 =>
                    new AesContentKey(
                        material,
                        state.CipherModeOf[parity] == DvbApiSetDescramblerMode.ModeCbc
                            ? state.Iv ?? AesContentKey.CissaIv.ToArray()
                            : []),
                _ => null,
            };
        }
        catch (ArgumentException exception)
        {
            LogBadKey(ChannelName, exception.Message);
            return null;
        }
    }

    private void OnKeyArrived(int index, int parity)
    {
        var pair = Volatile.Read(ref _keys[index]);
        var algorithm = (parity == 0 ? pair?.Even : pair?.Odd)?.Algorithm;
        LogKey(ChannelName, index, parity == 0 ? "even" : "odd", algorithm ?? "unusable");

        // A control word that could not be made into a key descrambles nothing. Counting it as
        // the first key let a channel open on scrambled packets, and the worker spent its whole
        // probe timeout on noise before anybody was told why; the problem that was reported for
        // it stands instead, and is what the opening fails with.
        if (algorithm is null)
        {
            return;
        }

        Volatile.Write(ref _keyAt[parity], _time.GetTimestamp());

        var first = Interlocked.Exchange(ref _lastKeyedIndex, index) < 0;
        if (first)
        {
            LogFirstKeyTimeline();
        }

        ChangeStatus(status =>
        {
            // A key is the answer to "no control word" and to any trouble with the connection.
            var problem = status.Problem?.Code is DescramblerErrors.NoControlWordCode or DescramblerErrors.ConnectionLostCode
                or DescramblerErrors.UnreachableCode or DescramblerErrors.HandshakeFailedCode
                ? null
                : status.Problem;
            return (status with { Algorithm = algorithm ?? status.Algorithm, Problem = problem }, first || problem != status.Problem);
        });
    }

    private void OnEcmInfo(DvbApiEcmInfo info)
    {
        var answer = new EcmAnswer(
            info.CaId,
            info.Pid,
            info.ProviderId,
            info.EcmTimeMilliseconds,
            info.CardSystem,
            info.Reader,
            info.Source,
            info.Protocol,
            info.Hops);
        LogEcmInfo(ChannelName, info.CaId, info.Pid, info.ProviderId, info.CardSystem, info.Reader, info.Source, info.Protocol, info.EcmTimeMilliseconds, info.Hops);
        ChangeStatus(status => (status with { LastEcm = answer, EcmTimes = status.EcmTimes.With(info.EcmTimeMilliseconds) }, status.LastEcm is null));
    }

    // -------------------------------------------------------------------------------------------
    // The connection, from the client
    // -------------------------------------------------------------------------------------------

    /// <summary>The server is about to get the CA PMT on a new connection.</summary>
    public void OnConnected()
    {
        ChangeStatus(status => status.Problem?.Code is DescramblerErrors.ConnectionLostCode or DescramblerErrors.UnreachableCode
            or DescramblerErrors.HandshakeFailedCode
                ? (status with { Problem = null }, true)
                : (status, false));

        RestartControlWordTimer();
    }

    /// <summary>
    /// The server forgot the service with the connection: its filters are gone and will be set
    /// again. The keys stay; they are still right until the next key change.
    /// </summary>
    public void OnConnectionLost(string reason)
    {
        bool hadPids;
        lock (_stateLock)
        {
            hadPids = _filters.Pids.Count > 0;
            Volatile.Write(ref _filters, FilterSet.Empty);
        }

        if (hadPids)
        {
            RequiredPidsChanged?.Invoke(this, EventArgs.Empty);
        }

        lock (_stateLock)
        {
            _controlWordTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        ReportProblem(DescramblerErrors.ConnectionLost(ChannelName, _client.Endpoint, reason));
    }

    public void OnReconnectFailed(TunerException exception) => ReportProblem(exception);

    public string DescribeMissingKey(int pid, bool odd)
    {
        string Ago(long at) => at == 0
            ? "never"
            : string.Create(CultureInfo.InvariantCulture, $"{_time.GetElapsedTime(at).TotalSeconds:0.0} s ago");

        var filters = Volatile.Read(ref _filters);
        var ecmFilters = string.Join(", ", Volatile.Read(ref _programMap).EcmPids.Keys.Select(pid => string.Create(
            CultureInfo.InvariantCulture,
            $"pid {pid} {(filters.Wants(pid) ? "filtered" : "not filtered")}")));

        // Which descrambler the pid is on, as the server last said, and which descramblers have
        // keys: a key that arrived for another index than the pid's is a key this pid never sees.
        var mapped = pid is >= 0 and < PidCount ? Volatile.Read(ref _pidToIndex[pid]) : 0;
        var mapping = mapped switch
        {
            0 => string.Create(CultureInfo.InvariantCulture, $"pid {pid} was never given an index and uses the last keyed one ({Volatile.Read(ref _lastKeyedIndex)})"),
            < 0 => string.Create(CultureInfo.InvariantCulture, $"pid {pid} was switched off by the server {Ago(Volatile.Read(ref _pidMappedAt[pid]))}"),
            _ => string.Create(CultureInfo.InvariantCulture, $"pid {pid} is on index {mapped - 1} since {Ago(Volatile.Read(ref _pidMappedAt[pid]))}"),
        };
        var keyed = string.Join(", ", Enumerable.Range(0, MaxIndexes)
            .Select(index => (Index: index, Pair: Volatile.Read(ref _keys[index])))
            .Where(entry => entry.Pair is not null)
            .Select(entry => string.Create(
                CultureInfo.InvariantCulture,
                $"index {entry.Index} {(entry.Pair!.Even is null ? "-" : "even")}/{(entry.Pair.Odd is null ? "-" : "odd")}")));

        return string.Create(
            CultureInfo.InvariantCulture,
            $"no {(odd ? "odd" : "even")} key; {mapping}; keys on {(keyed.Length == 0 ? "no index" : keyed)}; the even key came {Ago(Volatile.Read(ref _keyAt[0]))}, the odd {Ago(Volatile.Read(ref _keyAt[1]))}. "
            + $"ECM 80 seen {Ago(Volatile.Read(ref _ecmSeenAt[0]))}, sent {Ago(Volatile.Read(ref _ecmSentAt[0]))}; "
            + $"ECM 81 seen {Ago(Volatile.Read(ref _ecmSeenAt[1]))}, sent {Ago(Volatile.Read(ref _ecmSentAt[1]))}; "
            + $"{ecmFilters}; server {(_client.IsConnected ? "connected" : "not connected")}");
    }

    /// <summary>
    /// Where the wait for the first key went: to the server asking for the ECM, to the ECM
    /// reaching it, or to the card answering. Each has its own remedy.
    /// </summary>
    private void LogFirstKeyTimeline()
    {
        var since = Interlocked.Read(ref _watchingSince);
        if (since == 0)
        {
            return;
        }

        var now = _time.GetTimestamp();
        var filterAt = Interlocked.Read(ref _firstEcmFilterAt);
        var sentAt = Interlocked.Read(ref _firstEcmSentAt);
        var total = (long)_time.GetElapsedTime(since, now).TotalMilliseconds;
        var filter = filterAt == 0 ? -1 : (long)_time.GetElapsedTime(since, filterAt).TotalMilliseconds;
        var sent = sentAt == 0 ? -1 : (long)_time.GetElapsedTime(since, sentAt).TotalMilliseconds;
        var answer = sentAt == 0 ? -1 : (long)_time.GetElapsedTime(sentAt, now).TotalMilliseconds;

        LogKeyTimeline(ChannelName, total, filter, sent, answer);
    }

    private void RestartControlWordTimer()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        lock (_stateLock)
        {
            _controlWordTimer?.Dispose();
            _controlWordTimer = _time.CreateTimer(
                static state => ((DvbApiServiceDescrambler)state!).OnControlWordTimeout(),
                this,
                _client.Timings.ControlWordTimeout,
                Timeout.InfiniteTimeSpan);
        }
    }

    private void OnControlWordTimeout()
    {
        if (HasKeys || Volatile.Read(ref _disposed) != 0 || !_client.IsConnected)
        {
            return;
        }

        var timeout = _client.Timings.ControlWordTimeout;
        var ecmSections = Interlocked.Read(ref _ecmSections);
        var programMap = Volatile.Read(ref _programMap);
        var ecmPids = string.Join(", ", programMap.EcmPids.Select(pair => string.Create(
            CultureInfo.InvariantCulture,
            $"pid {pair.Key} (CAID {string.Join("/", pair.Value.Select(caid => caid.ToString("X4", CultureInfo.InvariantCulture)))})")));
        var detail = (Volatile.Read(ref _ecmFilterSeen) != 0, ecmSections) switch
        {
            (false, _) when programMap.EcmPids.Count == 0 =>
                "The program map lists no CA descriptors, so the descrambling server was not told how the channel is scrambled.",
            (false, _) => $"The descrambling server set no section filter for its ECMs ({ecmPids}): it has no reader for these CAIDs, or the service is excluded there.",
            (true, 0) => $"The descrambling server asked for ECMs ({ecmPids}), but none arrived from the tuner on those pids.",
            _ => string.Create(
                CultureInfo.InvariantCulture,
                $"{ecmSections} ECMs were forwarded ({ecmPids}) and none was answered with a control word."),
        };

        var lastEcm = Volatile.Read(ref _status).LastEcm;
        if (lastEcm is not null)
        {
            detail += string.Create(
                CultureInfo.InvariantCulture,
                $" The last answered ECM was CAID {lastEcm.CaId:X4} from reader '{lastEcm.Reader}' ({lastEcm.Protocol}).");

            // Answered, yet no key: the descrambling server gave the stream pids to another client
            // on the same ca device (see CaProgramMap), or its stream relay kept the key.
            var device = CaProgramMap.DeviceNumber(Slot);
            detail += string.Create(
                CultureInfo.InvariantCulture,
                $" The descrambling server answered the ECM but handed over no control word. It does that when another dvbapi client descrambles the same channel as ca device {device} too, or when its stream relay keeps the key.");
        }

        var problem = DescramblerErrors.NoControlWord(ChannelName, _client.Endpoint, timeout, detail);
        LogNoControlWord(problem.Message);
        ReportProblem(problem);
    }

    private void ReportProblem(TunerException problem) => ChangeStatus(status => (status with { Problem = problem }, true));

    /// <summary>
    /// Changes the status under a lock - the reader and the stream thread both change it - and
    /// raises <see cref="StatusChanged"/> outside of it when the change says so.
    /// </summary>
    private void ChangeStatus(Func<DescramblingStatus, (DescramblingStatus Status, bool Notify)> change)
    {
        bool notify;
        lock (_statusLock)
        {
            (var next, notify) = change(_status);
            Volatile.Write(ref _status, next);
        }

        if (notify)
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_stateLock)
        {
            _controlWordTimer?.Dispose();
        }

        await _client.RemoveServiceAsync(this).ConfigureAwait(false);
        LogStopped(ChannelName, Slot);
    }

    // -------------------------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------------------------

    /// <summary>The keys of one descrambler index, as <see cref="Process"/> sees them.</summary>
    private sealed record KeyPair(ContentKey? Even, ContentKey? Odd);

    /// <summary>What the server said about one descrambler index, reader side.</summary>
    private sealed class IndexState
    {
        public int Algorithm { get; set; } = DvbApiSetDescramblerMode.AlgorithmCsa;

        public int CipherMode { get; set; } = DvbApiSetDescramblerMode.ModeEcb;

        /// <summary>The algorithm and cipher mode each parity's control word arrived under.</summary>
        public int[] AlgorithmOf { get; } = [DvbApiSetDescramblerMode.AlgorithmCsa, DvbApiSetDescramblerMode.AlgorithmCsa];

        public int[] CipherModeOf { get; } = [DvbApiSetDescramblerMode.ModeEcb, DvbApiSetDescramblerMode.ModeEcb];

        /// <summary>Takes a parity's new control word, under the mode the server last set.</summary>
        public void SetMaterial(int parity, byte[] material)
        {
            Material[parity] = material;
            AlgorithmOf[parity] = Algorithm;
            CipherModeOf[parity] = CipherMode;
        }

        public byte[]? Iv { get; set; }

        /// <summary>The raw key of each parity: an 8 byte control word, or a 16 byte AES key.</summary>
        public byte[]?[] Material { get; } = new byte[]?[2];

        /// <summary>The iCAM mode of the ECM each parity's control word came from.</summary>
        public byte[] EcmMode { get; } = new byte[2];
    }

    /// <summary>The section filters the server has set, immutable.</summary>
    private sealed class FilterSet
    {
        public static readonly FilterSet Empty = new([]);

        private readonly DemuxFilter[] _filters;
        private readonly bool[] _wanted = new bool[PidCount];

        private FilterSet(DemuxFilter[] filters)
        {
            _filters = filters;
            foreach (var filter in filters)
            {
                _wanted[filter.Pid] = true;
            }

            Pids = [.. filters.Select(filter => filter.Pid).Distinct().Order()];
        }

        public IReadOnlyList<int> Pids { get; }

        public bool Wants(int pid) => _wanted[pid];

        public IEnumerable<DemuxFilter> OnPid(int pid)
        {
            foreach (var filter in _filters)
            {
                if (filter.Pid == pid)
                {
                    yield return filter;
                }
            }
        }

        public FilterSet With(DemuxFilter filter) =>
            new([.. _filters.Where(f => f.Demux != filter.Demux || f.Filter != filter.Filter), filter]);

        public FilterSet Without(int demux, int filter) =>
            _filters.Any(f => f.Demux == demux && f.Filter == filter)
                ? new FilterSet([.. _filters.Where(f => f.Demux != demux || f.Filter != filter)])
                : this;
    }

    /// <summary>The packets of one call that share a key. Reused between calls.</summary>
    private sealed class Batch
    {
        private CsaPayload[] _payloads = new CsaPayload[64];
        private int[] _packetOffsets = new int[64];

        public ContentKey Key { get; private set; } = null!;

        public int Count { get; private set; }

        public ReadOnlySpan<CsaPayload> Payloads => _payloads.AsSpan(0, Count);

        public ReadOnlySpan<int> PacketOffsets => _packetOffsets.AsSpan(0, Count);

        public void Reset(ContentKey key)
        {
            Key = key;
            Count = 0;
        }

        public void Add(int packetOffset, int payloadOffset, int payloadLength)
        {
            if (Count == _payloads.Length)
            {
                Array.Resize(ref _payloads, Count * 2);
                Array.Resize(ref _packetOffsets, Count * 2);
            }

            _payloads[Count] = new CsaPayload(payloadOffset, payloadLength);
            _packetOffsets[Count] = packetOffset;
            Count++;
        }
    }

    // -------------------------------------------------------------------------------------------
    // Log
    // -------------------------------------------------------------------------------------------

    [LoggerMessage(Level = LogLevel.Information, Message = "{Channel}: first key {Total} ms after the CA PMT - the descrambling server asked for the ECM after {Filter} ms, the first ECM reached it after {Sent} ms, and its answer took {Answer} ms (-1: did not happen)")]
    private partial void LogKeyTimeline(string channel, long total, long filter, long sent, long answer);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Channel}: the descrambling server filters pid {Pid} for table id {TableId:X2} mask {TableMask:X2} (demux {Demux}, filter {Filter})")]
    private partial void LogFilterSet(string channel, int demux, int filter, int pid, byte tableId, byte tableMask);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Channel}: the descrambling server stopped demux {Demux} filter {Filter}")]
    private partial void LogFilterStopped(string channel, int demux, int filter);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Channel}: pid {Pid} is descrambled with index {Index}")]
    private partial void LogPidMapped(string channel, int pid, int index);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Channel}: new {Parity} key for index {Index}, {Algorithm}")]
    private partial void LogKey(string channel, int index, string parity, string algorithm);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Channel}: ECM CAID {CaId:X4} pid {Pid} provider {Provider:X6} answered by {CardSystem} reader '{Reader}' from {Source} ({Protocol}) in {EcmTime} ms, {Hops} hops")]
    private partial void LogEcmInfo(string channel, int caId, int pid, uint provider, string cardSystem, string reader, string source, string protocol, uint ecmTime, int hops);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Channel}: iCAM is active, CAID {CaId:X4}, ECM pid {Pid}, ECM mode byte {Mode}")]
    private partial void LogIcam(string channel, int caId, int pid, int mode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Channel}: program map version {Version} sent to the descrambling server")]
    private partial void LogProgramMapUpdated(string channel, int version);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Channel}: the descrambling server used descrambler index {Index}, which is outside 0 to 63; ignored")]
    private partial void LogIndexOutOfRange(string channel, int index);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Channel}: the descrambling server sent a key that cannot be used: {Reason}")]
    private partial void LogBadKey(string channel, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Message}")]
    private partial void LogNoControlWord(string message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Channel}: descrambling stopped, slot {Slot} free")]
    private partial void LogStopped(string channel, int slot);
}
