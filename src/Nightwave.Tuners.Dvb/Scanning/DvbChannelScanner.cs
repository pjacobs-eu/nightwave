using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb.Channels;
using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.Streaming;
using Nightwave.Tuners.Dvb.Transport;
using Nightwave.Tuners.Dvb.Tuning;
using Microsoft.Extensions.Logging;

namespace Nightwave.Tuners.Dvb.Scanning;

/// <summary>
/// Finds channels by tuning. A satellite scan checks which satellite its input receives, starts
/// from a few known transponders of it, reads each one's NIT to learn the rest of the network,
/// and reads PAT, PMT and SDT on every transponder it learns of. A cable scan searches the
/// raster until one transponder locks and lets its NIT do the rest; a terrestrial scan reads
/// every UHF multiplex that locks.
/// </summary>
internal sealed partial class DvbChannelScanner : IChannelScanner, IDisposable
{
    private readonly DeviceDirectory _devices;
    private readonly ChannelStore _store;
    private readonly TunerPool _pool;
    private readonly ITsTransportFactory _transports;
    private readonly InputDetector _detector;
    private readonly ScanTimings _timings;
    private readonly TimeProvider _time;
    private readonly ILogger<DvbChannelScanner> _logger;
    private readonly SemaphoreSlim _running = new(1, 1);

    public DvbChannelScanner(
        DeviceDirectory devices,
        ChannelStore store,
        TunerPool pool,
        ITsTransportFactory transports,
        InputDetector detector,
        ScanTimings timings,
        TimeProvider time,
        ILogger<DvbChannelScanner> logger)
    {
        _devices = devices;
        _store = store;
        _pool = pool;
        _transports = transports;
        _detector = detector;
        _timings = timings;
        _time = time;
        _logger = logger;
    }

    public string ProviderId => DvbProvider.Id;

    public void Dispose() => _running.Dispose();

    /// <remarks>
    /// Checks the inputs of every device with satellite tuners first, where that is due (see
    /// <see cref="InputDetector.IsDue"/>): a few seconds of one tuner the first time, nothing
    /// after that until the check is a week old - or found nothing, in which case it is repeated
    /// after a couple of minutes, so reloading the list after fixing a cable shows the dish.
    /// </remarks>
    public async Task<IReadOnlyList<ScanTarget>> GetTargetsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var device in await _devices.GetDevicesAsync(refresh: true, cancellationToken).ConfigureAwait(false))
        {
            await _detector.EnsureAsync(device, cancellationToken).ConfigureAwait(false);
        }

        var devices = await _devices.GetDevicesAsync(refresh: false, cancellationToken).ConfigureAwait(false);
        var targets = new List<ScanTarget>();

        foreach (var input in ScanInput.From(devices))
        {
            var (known, lastScanned) = await _store.DescribeTargetAsync(input.TargetId, cancellationToken).ConfigureAwait(false);

            targets.Add(new ScanTarget
            {
                Id = input.TargetId,
                Name = input.Name,
                DeliverySystem = input.Delivery,
                KnownChannels = known,
                LastScannedAt = lastScanned,
                Error = input.Error,
            });
        }

        return targets;
    }

    public async Task<ScanResult> ScanAsync(
        ScanRequest request,
        IProgress<ScanProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);

        if (!await _running.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw DvbErrors.ScanBusy();
        }

        try
        {
            var devices = await _devices.GetDevicesAsync(refresh: false, cancellationToken).ConfigureAwait(false);
            var input = ScanInput.From(devices).FirstOrDefault(candidate => string.Equals(candidate.TargetId, request.TargetId, StringComparison.Ordinal))
                ?? throw DvbErrors.TargetMissing(request.TargetId);

            if (input.Error is { } error)
            {
                throw DvbErrors.TargetUnusable(input.Name, error);
            }

            LogScanStarted(input.Name, request.Mode);

            var reservation = _pool.ReserveForScan(input.Device, input.Delivery, input.Name);

            await using (reservation.ConfigureAwait(false))
            {
                return await ScanInputAsync(input, request, progress, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task<ScanResult> ScanInputAsync(
        ScanInput input,
        ScanRequest request,
        IProgress<ScanProgress> progress,
        CancellationToken cancellationToken)
    {
        var run = new ScanRun(request, progress);

        try
        {
            switch (input.Delivery)
            {
                case DeliverySystem.Satellite:
                    input = await VerifyInputAsync(input, run, cancellationToken).ConfigureAwait(false);
                    run.Input = input;
                    run.Plan.AddRange(input.StartTransponders());

                    if (run.Plan.Count == 0)
                    {
                        throw DvbErrors.NoTransponders(input.Name, input.Position?.ToString() ?? "this input");
                    }

                    break;

                case DeliverySystem.Cable:
                    run.Input = input;
                    await SearchCableAsync(run, cancellationToken).ConfigureAwait(false);
                    break;

                case DeliverySystem.Terrestrial:
                    run.Input = input;
                    await SearchTerrestrialAsync(run, cancellationToken).ConfigureAwait(false);
                    break;
            }

            await ScanPlanAsync(run, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (request.Mode == ScanMode.Merge && run.Found.Count > 0)
            {
                var partial = Finish(run.Found.Values, run.LogicalChannels, request);
                await _store.ApplyScanAsync(input.TargetId, partial, ScanMode.Merge, null, CancellationToken.None).ConfigureAwait(false);
            }

            LogScanCancelled(input.Name, run.Found.Count);
            throw;
        }

        var channels = Finish(run.Found.Values, run.LogicalChannels, request);
        var removed = await _store.ApplyScanAsync(input.TargetId, channels, request.Mode, _time.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);

        progress.Report(Progress(channels, request, run.Plan.Count, run.Next, null) with { Fraction = 1 });
        LogScanFinished(input.Name, channels.Count, removed, run.Failures.Count);

        return new ScanResult
        {
            TelevisionFound = channels.Count(channel => channel.Kind == ChannelKind.Television),
            RadioFound = channels.Count(channel => channel.Kind == ChannelKind.Radio),
            EncryptedFound = channels.Count(channel => channel.Scrambled),
            Removed = request.Mode == ScanMode.Replace ? removed : 0,
            Failures = run.Failures,
        };
    }

    /// <summary>
    /// Checks, on the scan's own tuner, that the input still receives the satellite it was
    /// detected with - the one it had is tried first, so this is one tune when nothing changed.
    /// A dish that was turned is scanned where it points now.
    /// </summary>
    private async Task<ScanInput> VerifyInputAsync(ScanInput input, ScanRun run, CancellationToken cancellationToken)
    {
        if (input.Source is not { } source)
        {
            return input;
        }

        DetectedInput detected;

        try
        {
            detected = await _detector.VerifyInputAsync(
                input.Device,
                source,
                input.Position,
                tuning => run.Report(tuning.Describe(), 0, 0),
                cancellationToken).ConfigureAwait(false);
        }
        catch (TunerTransportException error)
        {
            throw DvbErrors.TargetUnusable(input.Name, error.Message);
        }

        if (detected.Position is not { } tenths)
        {
            throw DvbErrors.TargetUnusable(input.Name, $"no satellite is received on input {source} any more: {detected.Failure}");
        }

        var position = new OrbitalPosition(tenths);

        if (input.Position is { } before && before != position)
        {
            LogInputMoved(input.Name, before.ToString(), position.ToString());
        }

        return input with { Position = position, LockedOn = detected.LockedOn };
    }

    /// <summary>
    /// Searches the cable raster until a transponder locks and carries a NIT; from there the
    /// network drives the scan, as on satellite. Each frequency is tried with the symbol rate and
    /// modulation that locked last first, and one where the tuner reports no signal level at all
    /// is left after the first try: no setting finds a carrier that is not there.
    /// </summary>
    private async Task SearchCableAsync(ScanRun run, CancellationToken cancellationToken)
    {
        var frequencies = BlindScan.CableFrequenciesKhz;
        var order = new CableSettingsOrder();
        var locked = 0;
        string? firstMiss = null;

        for (var index = 0; index < frequencies.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var setting in order.Current)
            {
                var tuning = BlindScan.Cable(frequencies[index], setting);
                run.Report(tuning.Describe(), frequencies.Count, index);

                var (result, error) = await TryScanAsync(run.Input, tuning, _timings.BlindLock, cancellationToken).ConfigureAwait(false);

                if (result is not null)
                {
                    order.Locked(setting);
                    locked++;
                    run.TakeSearched(tuning, result);

                    if (result.Network.Any(run.Input.Follows))
                    {
                        LogNetworkFound(run.Input.Name, tuning, run.Plan.Count - run.Next);
                        return;
                    }

                    break;
                }

                firstMiss ??= $"{tuning.Describe()}: {error!.Message}";

                if (error!.Signal is { Level: 0, Locked: false })
                {
                    break;
                }
            }
        }

        run.Failures.Add(locked == 0
            ? $"No cable transponder locked on any of {frequencies.Count} frequencies between 114 and 858 MHz with 6900 or 6875 kS/s and 64- or 256-QAM. Check the cable from the wall outlet to the device (first: {firstMiss})."
            : $"{locked} cable transponders locked, but none carried a network information table to learn the others from.");
    }

    /// <summary>
    /// Tries every UHF channel from 21 to 48, DVB-T2 first and DVB-T second, and reads every
    /// multiplex that locks. Where the tuner reports no signal level on DVB-T2, DVB-T is not
    /// tried: the carrier is not there either way.
    /// </summary>
    private async Task SearchTerrestrialAsync(ScanRun run, CancellationToken cancellationToken)
    {
        var frequencies = BlindScan.TerrestrialFrequenciesKhz;
        var locked = 0;
        string? firstMiss = null;

        for (var index = 0; index < frequencies.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var tuning in BlindScan.Terrestrial(frequencies[index]))
            {
                run.Report(tuning.Describe(), frequencies.Count, index);

                var (result, error) = await TryScanAsync(run.Input, tuning, _timings.BlindLock, cancellationToken).ConfigureAwait(false);

                if (result is not null)
                {
                    locked++;
                    run.TakeSearched(tuning, result);
                    break;
                }

                firstMiss ??= $"{tuning.Describe()}: {error!.Message}";

                if (error!.Signal is { Level: 0, Locked: false })
                {
                    break;
                }
            }
        }

        if (locked == 0)
        {
            run.Failures.Add(
                $"No DVB-T2 or DVB-T multiplex locked on any UHF channel from 21 to 48 (474 to 690 MHz). Check the aerial and its cable (first: {firstMiss}).");
        }
    }

    /// <summary>Scans what is left in the plan, learning more transponders from every NIT.</summary>
    private async Task ScanPlanAsync(ScanRun run, CancellationToken cancellationToken)
    {
        while (run.Next < run.Plan.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var alternatives = run.Plan[run.Next];
            run.Report(alternatives[0].Describe(), run.Plan.Count, run.Next);

            var (result, used, failure) = await ScanTransponderAsync(run, alternatives, cancellationToken).ConfigureAwait(false);
            run.Next++;

            if (result is null || used is null)
            {
                run.Failures.Add($"{alternatives[0].Describe()}: {failure}");
                LogTransponderFailed(run.Input.Name, alternatives[0].Describe(), failure ?? "no data");
                run.Report(null, run.Plan.Count, run.Next);
                continue;
            }

            var added = run.Take(used, result);

            if (added == 0)
            {
                run.Failures.Add($"{used.Describe()}: locked, but no television or radio service was found on it.");
            }

            LogTransponderScanned(run.Input.Name, used, result.Services.Count, added);
            run.Report(null, run.Plan.Count, run.Next);
        }
    }

    /// <summary>Tunes one transponder, trying each alternative until one locks.</summary>
    private async Task<(MuxScanResult? Result, TuningParameters? Used, string? Failure)> ScanTransponderAsync(
        ScanRun run,
        IReadOnlyList<TuningParameters> alternatives,
        CancellationToken cancellationToken)
    {
        string? failure = null;

        foreach (var tuning in alternatives)
        {
            var (result, error) = await TryScanWithRetriesAsync(run, tuning, cancellationToken).ConfigureAwait(false);

            if (result is not null)
            {
                return (result, tuning, null);
            }

            failure = failure is null ? error!.Message : $"{failure} / {tuning.Describe()}: {error!.Message}";
        }

        return (null, null, failure);
    }

    /// <summary>
    /// Scans a transponder, again where it locked and yet gave nothing: right after a tune, a
    /// Kathrein EXIP-418 now and then sends what it still had and then nothing for as long as one
    /// waits, and a new session cures that. The last try does without the NIT's transport stream,
    /// in case that is what is wrong. A transponder without a lock is not asked twice.
    /// </summary>
    private async Task<(MuxScanResult? Result, TunerTransportException? Error)> TryScanWithRetriesAsync(
        ScanRun run,
        TuningParameters tuning,
        CancellationToken cancellationToken)
    {
        int? expected = run.ExpectedStreams.TryGetValue(tuning, out var stream) ? stream : null;
        (MuxScanResult? Result, TunerTransportException? Error) outcome = default;

        foreach (var attempt in new[] { expected, expected, null })
        {
            outcome = await TryScanAsync(run.Input, tuning, null, cancellationToken, attempt).ConfigureAwait(false);

            if (outcome.Result is not null || outcome.Error?.Signal is { Locked: false })
            {
                break;
            }

            // It also shuts the door on sessions that follow each other too quickly.
            await Task.Delay(_timings.Retry, cancellationToken).ConfigureAwait(false);
        }

        return outcome;
    }

    private async Task<(MuxScanResult? Result, TunerTransportException? Error)> TryScanAsync(
        ScanInput input,
        TuningParameters tuning,
        TimeSpan? lockWindow,
        CancellationToken cancellationToken,
        int? expectedTransportStream = null)
    {
        var transport = _transports.Create(input.Device, new TuneRequest(tuning, input.Source));

        try
        {
            var result = await MuxScanner.ScanAsync(
                transport,
                _timings,
                readNetwork: input.Delivery != DeliverySystem.Terrestrial,
                cancellationToken,
                lockWindow,
                expectedTransportStream).ConfigureAwait(false);

            return (result, null);
        }
        catch (TunerTransportException error)
        {
            return (null, error);
        }
        finally
        {
            await transport.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Numbers from the NIT, and the filters the request asked for.</summary>
    private static List<StoredChannel> Finish(
        IEnumerable<StoredChannel> channels,
        Dictionary<(int Network, int TransportStream, int Service), int> logicalChannels,
        ScanRequest request) =>
        [
            .. channels
                .Where(channel => request.IncludeEncrypted || !channel.Scrambled)
                .Where(channel => request.IncludeRadio || channel.Kind != ChannelKind.Radio)
                .Select(channel => logicalChannels.TryGetValue(
                        (channel.OriginalNetworkId, channel.TransportStreamId, channel.ServiceId),
                        out var number)
                    ? channel with { Number = number.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                    : channel),
        ];

    private static ScanProgress Progress(
        IEnumerable<StoredChannel> found,
        ScanRequest request,
        int total,
        int done,
        string? current)
    {
        var kept = found
            .Where(channel => request.IncludeEncrypted || !channel.Scrambled)
            .Where(channel => request.IncludeRadio || channel.Kind != ChannelKind.Radio)
            .ToList();

        return new ScanProgress
        {
            Fraction = total == 0 ? 0 : Math.Clamp(done / (double)total, 0, 1),
            Current = current,
            TransponderCount = total,
            TranspondersDone = done,
            TelevisionFound = kept.Count(channel => channel.Kind == ChannelKind.Television),
            RadioFound = kept.Count(channel => channel.Kind == ChannelKind.Radio),
            EncryptedFound = kept.Count(channel => channel.Scrambled),
        };
    }

    [LoggerMessage(EventId = 9860, Level = LogLevel.Information, Message = "Channel scan of {Target} started ({Mode}).")]
    private partial void LogScanStarted(string target, ScanMode mode);

    [LoggerMessage(EventId = 9861, Level = LogLevel.Information, Message = "Scanned {Transponder} on {Target}: {Services} services, {Channels} new channels.")]
    private partial void LogTransponderScanned(string target, TuningParameters transponder, int services, int channels);

    [LoggerMessage(EventId = 9862, Level = LogLevel.Warning, Message = "Scanning {Transponder} on {Target} failed: {Reason}")]
    private partial void LogTransponderFailed(string target, string transponder, string reason);

    [LoggerMessage(EventId = 9863, Level = LogLevel.Information, Message = "Channel scan of {Target} finished: {Channels} channels kept, {Removed} removed, {Failures} transponders failed.")]
    private partial void LogScanFinished(string target, int channels, int removed, int failures);

    [LoggerMessage(EventId = 9865, Level = LogLevel.Warning, Message = "{Target} receives {Now} now, not {Before} as detected before; it is scanned as {Now}.")]
    private partial void LogInputMovedCore(string target, string now, string before);

    private void LogInputMoved(string target, string before, string now) => LogInputMovedCore(target, now, before);

    [LoggerMessage(EventId = 9866, Level = LogLevel.Information, Message = "{Target}: {Transponder} locked and named its network; the search stops there and {Transponders} transponders from its NIT are scanned.")]
    private partial void LogNetworkFound(string target, TuningParameters transponder, int transponders);

    [LoggerMessage(EventId = 9864, Level = LogLevel.Information, Message = "Channel scan of {Target} cancelled after {Channels} channels.")]
    private partial void LogScanCancelled(string target, int channels);
}

/// <summary>What one scan has found and has still to do.</summary>
internal sealed class ScanRun(ScanRequest request, IProgress<ScanProgress> progress)
{
    public ScanInput Input { get; set; } = null!;

    /// <summary>The transponders to scan, and scanned: everything before <see cref="Next"/> is done.</summary>
    public List<IReadOnlyList<TuningParameters>> Plan { get; } = [];

    public int Next { get; set; }

    public Dictionary<string, StoredChannel> Found { get; } = new(StringComparer.Ordinal);

    /// <summary>The transport stream the NIT says each planned transponder carries.</summary>
    public Dictionary<TuningParameters, int> ExpectedStreams { get; } = [];

    public Dictionary<(int Network, int TransportStream, int Service), int> LogicalChannels { get; } = [];

    public Dictionary<int, int> NetworkIds { get; } = [];

    public List<string> Failures { get; } = [];

    /// <summary>Takes a transponder a search found and scanned, as if it had been planned.</summary>
    public void TakeSearched(TuningParameters tuning, MuxScanResult result)
    {
        Plan.Insert(Next, [tuning]);
        Next++;
        Take(tuning, result);
    }

    /// <summary>
    /// Takes what a transponder said: its services as channels, its NIT's transport streams
    /// into the plan where the scan follows them. Returns how many channels are new.
    /// </summary>
    public int Take(TuningParameters used, MuxScanResult result)
    {
        foreach (var stream in result.Network)
        {
            NetworkIds[stream.TransportStreamId] = stream.OriginalNetworkId;

            foreach (var (service, number) in stream.LogicalChannels)
            {
                LogicalChannels[(stream.OriginalNetworkId, stream.TransportStreamId, service)] = number;
            }

            if (Input.Follows(stream)
                && stream.Tuning is { } tuning
                && !Plan.Any(step => step.Any(known => known.IsSameTransponder(tuning))))
            {
                Plan.Add([tuning]);
                ExpectedStreams[tuning] = stream.TransportStreamId;
            }
        }

        var before = Found.Count;

        foreach (var service in result.Services)
        {
            if (ChannelFactory.FromService(Input, used, result, service, NetworkIds.GetValueOrDefault(result.TransportStreamId), null) is { } channel)
            {
                // Never a good entry for a bare one: a channel known by name stays what it is.
                if (service.Description is not null || !Found.ContainsKey(channel.Id))
                {
                    Found[channel.Id] = channel;
                }
            }
        }

        return Found.Count - before;
    }

    public void Report(string? current, int total, int done)
    {
        var kept = Found.Values
            .Where(channel => request.IncludeEncrypted || !channel.Scrambled)
            .Where(channel => request.IncludeRadio || channel.Kind != ChannelKind.Radio)
            .ToList();

        progress.Report(new ScanProgress
        {
            Fraction = total == 0 ? 0 : Math.Clamp(done / (double)total, 0, 1),
            Current = current,
            TransponderCount = total,
            TranspondersDone = done,
            TelevisionFound = kept.Count(channel => channel.Kind == ChannelKind.Television),
            RadioFound = kept.Count(channel => channel.Kind == ChannelKind.Radio),
            EncryptedFound = kept.Count(channel => channel.Scrambled),
        });
    }
}
