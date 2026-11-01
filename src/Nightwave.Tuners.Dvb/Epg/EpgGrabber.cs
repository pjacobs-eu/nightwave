using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb.Channels;
using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.Mpeg;
using Nightwave.Tuners.Dvb.Mpeg.Si;
using Nightwave.Tuners.Dvb.Streaming;
using Nightwave.Tuners.Dvb.Transport;
using Microsoft.Extensions.Logging;

namespace Nightwave.Tuners.Dvb.Epg;

/// <summary>How long the grabber stays on a transponder. Shorter in tests.</summary>
internal sealed record EpgGrabberTimings
{
    /// <summary>After start-up, before the first run: the server has other things to do first.</summary>
    public TimeSpan FirstRun { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>At least this long on a transponder: the schedule of the next days comes round slowly.</summary>
    public TimeSpan MinimumDwell { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>At most this long on a transponder.</summary>
    public TimeSpan MaximumDwell { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>No new section for this long, and the sections are going round again: done.</summary>
    public TimeSpan Quiet { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often what was collected is written to disk.</summary>
    public TimeSpan Flush { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>What one run of the grabber did.</summary>
internal sealed record EpgRunResult(int Tuned, int Skipped, int Preempted, int Busy);

/// <summary>
/// Reads the guide while the tuners are idle. Every few hours it tunes the transponders the
/// channel list knows, one after the other, and stays on each until its EIT sections come
/// round again - or until a viewer needs the tuner, who gets it at once.
/// </summary>
/// <remarks>
/// A satellite operator's transponders usually carry the schedule of the whole network in their
/// "EIT other" tables, so a transponder whose services all turned up in what an earlier one
/// sent is skipped: on Astra one or two transponders are the whole guide. Transponders being
/// watched are skipped too - their guide is read as they stream.
/// </remarks>
internal sealed partial class EpgGrabber : IBackgroundWork, IDisposable
{
    private readonly EpgService _epg;
    private readonly ChannelStore _channels;
    private readonly DeviceDirectory _devices;
    private readonly TunerPool _pool;
    private readonly ITsTransportFactory _transports;
    private readonly DvbSettingsProvider _settings;
    private readonly EpgGrabberTimings _timings;
    private readonly ILogger<EpgGrabber> _logger;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly SemaphoreSlim _trigger = new(0, 1);
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private Task? _flushing;

    public EpgGrabber(
        EpgService epg,
        ChannelStore channels,
        DeviceDirectory devices,
        TunerPool pool,
        ITsTransportFactory transports,
        DvbSettingsProvider settings,
        EpgGrabberTimings timings,
        ILogger<EpgGrabber> logger)
    {
        _epg = epg;
        _channels = channels;
        _devices = devices;
        _pool = pool;
        _transports = transports;
        _settings = settings;
        _timings = timings;
        _logger = logger;
    }

    public void Dispose()
    {
        _stop?.Dispose();
        _running.Dispose();
        _trigger.Dispose();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);
        _flushing = Task.Run(() => FlushLoopAsync(_stop.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_stop is not null)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }

        foreach (var task in new[] { _loop, _flushing })
        {
            if (task is not null)
            {
                await task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }

        // The guide is kept to save reading it again, not because it cannot be read again: a
        // shutdown that has run out of time loses the last minutes of it and nothing else. An
        // exception out of here would stop the host before it stopped anything after this -
        // the transcode workers among them.
        try
        {
            await _epg.Store.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            LogFlushSkipped(error.Message);
        }
    }

    /// <summary>Asks for a run now rather than at the next interval. Returns at once; a run takes minutes.</summary>
    public void Trigger()
    {
        if (_trigger.CurrentCount == 0)
        {
            try
            {
                _trigger.Release();
            }
            catch (SemaphoreFullException)
            {
                // Asked twice before the loop woke up; once is enough.
            }
        }
    }

    /// <summary>One run over every transponder. Returns at once, with nothing done, while another run is going.</summary>
    public async Task<EpgRunResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!await _running.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new EpgRunResult(0, 0, 0, 0);
        }

        try
        {
            return await RunAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _running.Release();
        }
    }

    private async Task LoopAsync(CancellationToken stop)
    {
        var wait = _timings.FirstRun;

        while (!stop.IsCancellationRequested)
        {
            try
            {
                await _trigger.WaitAsync(wait, stop).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var result = await RunOnceAsync(stop).ConfigureAwait(false);
                LogRun(result.Tuned, result.Skipped, result.Preempted, result.Busy);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                LogRunFailed(error.Message);
            }

            var settings = await _settings.GetAsync(stop).ConfigureAwait(false);
            wait = TimeSpan.FromHours(Math.Clamp(settings.EpgIntervalHours, 1, 168));
        }
    }

    /// <summary>
    /// Writes what tuners collected while they streamed. The grabber writes after every
    /// transponder by itself; this is for the guide that comes along with viewers.
    /// </summary>
    private async Task FlushLoopAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(_timings.Flush);

        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                try
                {
                    await _epg.Store.FlushAsync(stop).ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    LogRunFailed(error.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping; StopAsync writes one last time.
        }
    }

    private async Task<EpgRunResult> RunAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        if (!settings.EpgEnabled || settings.EpgMaxTuners <= 0)
        {
            return new EpgRunResult(0, 0, 0, 0);
        }

        await _epg.Store.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        var channels = await _channels.GetChannelsAsync(cancellationToken).ConfigureAwait(false);
        var devices = await _devices.GetDevicesAsync(refresh: false, cancellationToken).ConfigureAwait(false);
        var transponders = Transponders(channels);
        var covered = new HashSet<DvbServiceKey>();
        var queue = new Queue<Transponder>(transponders);
        var tuned = 0;
        var skipped = 0;
        var preempted = 0;
        var busy = 0;

        async Task WorkAsync()
        {
            while (true)
            {
                Transponder transponder;

                lock (queue)
                {
                    if (!queue.TryDequeue(out transponder!))
                    {
                        return;
                    }

                    if (transponder.Services.All(covered.Contains))
                    {
                        skipped++;
                        continue;
                    }
                }

                var outcome = await GrabAsync(transponder, devices, cancellationToken).ConfigureAwait(false);

                lock (queue)
                {
                    switch (outcome.Result)
                    {
                        case GrabResult.Tuned:
                            tuned++;
                            covered.UnionWith(outcome.Services);
                            break;
                        case GrabResult.Preempted:
                            preempted++;
                            covered.UnionWith(outcome.Services);
                            break;
                        case GrabResult.AlreadyTuned:
                            skipped++;
                            break;
                        default:
                            busy++;
                            break;
                    }
                }

                await _epg.Store.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var workers = Math.Clamp(settings.EpgMaxTuners, 1, 8);
        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => WorkAsync())).ConfigureAwait(false);

        _epg.Store.Prune();
        await _epg.Store.FlushAsync(cancellationToken).ConfigureAwait(false);

        return new EpgRunResult(tuned, skipped, preempted, busy);
    }

    private async Task<(GrabResult Result, IReadOnlyCollection<DvbServiceKey> Services)> GrabAsync(
        Transponder transponder,
        IReadOnlyList<TunerDevice> devices,
        CancellationToken cancellationToken)
    {
        var candidates = DvbTunerProvider.Candidates(transponder.Channel, devices).ToList();

        if (candidates.Any(candidate => _pool.IsTuned(candidate.Device, candidate.Request)))
        {
            return (GrabResult.AlreadyTuned, []);
        }

        foreach (var candidate in candidates)
        {
            var lease = _pool.TryReserveForGuide(candidate.Device, candidate.Request.Tuning.Delivery);
            if (lease is null)
            {
                continue;
            }

            await using (lease.ConfigureAwait(false))
            {
                var collector = _epg.CreateCollector();
                var transport = _transports.Create(candidate.Device, candidate.Request);

                await using (transport.ConfigureAwait(false))
                {
                    using var dwell = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Token);
                    dwell.CancelAfter(_timings.MaximumDwell);

                    try
                    {
                        await transport.StartAsync([EventInformationTable.Pid], dwell.Token).ConfigureAwait(false);
                        await ReadAsync(transport, collector, dwell.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        // The dwell is over, or a viewer took the tuner.
                    }
                    catch (TunerTransportException error)
                    {
                        LogTransponderFailed(transponder.Channel.Tuning!, error.Message);
                    }
                }

                LogTransponderRead(transponder.Channel.Tuning!, collector.NewSections, collector.ServicesWithSchedule.Count, lease.WasPreempted);

                return (lease.WasPreempted ? GrabResult.Preempted : GrabResult.Tuned, [.. collector.ServicesWithSchedule]);
            }
        }

        return (GrabResult.Busy, []);
    }

    /// <summary>Reads the EIT until the sections go round again with nothing new, or the dwell ends.</summary>
    private async Task ReadAsync(ITsTransport transport, EitCollector collector, CancellationToken cancellationToken)
    {
        var input = new byte[64 * 1024];
        var packets = new byte[input.Length + TransportPacket.Size];
        var aligner = new PacketAligner();
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (true)
        {
            var read = await transport.ReadAsync(input, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            var count = aligner.Push(input.AsSpan(0, read), packets);
            collector.Process(packets.AsSpan(0, count));

            if (clock.Elapsed >= _timings.MinimumDwell && collector.SinceLastNewSection >= _timings.Quiet)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The transponders of the channel list, one entry each, with the services on them. The
    /// transponders with the most services come first: they are the likeliest to carry the
    /// schedule of the rest.
    /// </summary>
    internal static IReadOnlyList<Transponder> Transponders(IEnumerable<StoredChannel> channels)
    {
        var transponders = new List<(StoredChannel First, List<StoredChannel> All)>();

        foreach (var channel in channels.Where(channel => channel.Tuning is not null))
        {
            var existing = transponders.FirstOrDefault(entry =>
                entry.First.Delivery == channel.Delivery
                && entry.First.OrbitalPosition == channel.OrbitalPosition
                && string.Equals(entry.First.DeviceId, channel.DeviceId, StringComparison.OrdinalIgnoreCase)
                && entry.First.Tuning!.IsSameTransponder(channel.Tuning!));

            if (existing.All is null)
            {
                transponders.Add((channel, [channel]));
            }
            else
            {
                existing.All.Add(channel);
            }
        }

        return
        [
            .. transponders
                .OrderByDescending(entry => entry.All.Count)
                .Select(entry => new Transponder(
                    entry.First,
                    [.. entry.All.Select(channel => new DvbServiceKey(channel.OriginalNetworkId, channel.TransportStreamId, channel.ServiceId))])),
        ];
    }

    [LoggerMessage(EventId = 9880, Level = LogLevel.Information, Message = "Guide run done: {Tuned} transponders read, {Skipped} skipped as covered or tuned, {Preempted} given up to viewers, {Busy} with no tuner free.")]
    private partial void LogRun(int tuned, int skipped, int preempted, int busy);

    [LoggerMessage(EventId = 9884, Level = LogLevel.Warning, Message = "The guide read since the last save was not written on shutdown: {Reason}")]
    private partial void LogFlushSkipped(string reason);

    [LoggerMessage(EventId = 9881, Level = LogLevel.Warning, Message = "The guide run failed: {Reason}")]
    private partial void LogRunFailed(string reason);

    [LoggerMessage(EventId = 9882, Level = LogLevel.Debug, Message = "Read the guide on {Transponder}: {Sections} new sections, schedules for {Services} services, taken by a viewer: {Preempted}.")]
    private partial void LogTransponderRead(Tuning.TuningParameters transponder, int sections, int services, bool preempted);

    [LoggerMessage(EventId = 9883, Level = LogLevel.Information, Message = "The guide could not be read on {Transponder}: {Reason}")]
    private partial void LogTransponderFailed(Tuning.TuningParameters transponder, string reason);

    private enum GrabResult
    {
        Tuned,
        Preempted,
        AlreadyTuned,
        Busy,
    }
}

/// <summary>One transponder of the channel list: a channel to tune it by, and the services on it.</summary>
internal sealed record Transponder(StoredChannel Channel, IReadOnlyList<DvbServiceKey> Services);
