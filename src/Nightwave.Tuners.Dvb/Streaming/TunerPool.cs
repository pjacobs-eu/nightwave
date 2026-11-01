using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb.Channels;
using Nightwave.Tuners.Dvb.Descrambling;
using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.Epg;
using Nightwave.Tuners.Dvb.Transport;
using Microsoft.Extensions.Logging;

namespace Nightwave.Tuners.Dvb.Streaming;

/// <summary>A device and what to tune on it for one channel: one of the ways a channel can be received.</summary>
internal sealed record TuneCandidate(TunerDevice Device, TuneRequest Request);

/// <summary>
/// Every tuner in use, and who uses it. Hands out tuners the way a receiver with several would:
/// a channel somebody is already watching is shared, a channel on a transponder that is already
/// tuned joins that tuner, and only then is a new tuner taken. When there is none, a client
/// changing channel takes over the tuner of their one running stream; otherwise the answer is
/// an error that says so, never a wait.
/// </summary>
internal sealed partial class TunerPool : IAsyncDisposable
{
    private readonly ITsTransportFactory _transports;
    private readonly IServiceDescramblerFactory _descramblers;
    private readonly DescramblerSlots _slots;
    private readonly FeedTimings _timings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly List<TunedMultiplex> _multiplexes = [];
    private readonly List<ScanLease> _scans = [];
    private readonly List<GuideLease> _guideLeases = [];
    private readonly List<TunerViewer> _viewers = [];
    private readonly EpgService? _guide;

    public TunerPool(
        ITsTransportFactory transports,
        IServiceDescramblerFactory descramblers,
        DescramblerSlots slots,
        FeedTimings timings,
        ILoggerFactory loggerFactory,
        EpgService? guide = null)
    {
        _guide = guide;
        _transports = transports;
        _descramblers = descramblers;
        _slots = slots;
        _timings = timings;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TunerPool>();
    }

    /// <inheritdoc cref="OpenAsync(StoredChannel, IReadOnlyList{TuneCandidate}, string?, CancellationToken)"/>
    public Task<ServiceLease> OpenAsync(
        StoredChannel channel,
        IReadOnlyList<TuneCandidate> candidates,
        CancellationToken cancellationToken) =>
        OpenAsync(channel, candidates, null, cancellationToken);

    /// <summary>
    /// Tunes a channel on the first candidate that can take it and waits until it can be
    /// watched. Candidates are tried in the order given.
    /// </summary>
    /// <param name="channel">The channel to tune.</param>
    /// <param name="candidates">The ways the channel can be received.</param>
    /// <param name="client">
    /// Who is asking, where a new stream of theirs may replace their running one; null where it
    /// may not.
    /// </param>
    /// <param name="cancellationToken">Ends the wait for the channel.</param>
    public async Task<ServiceLease> OpenAsync(
        StoredChannel channel,
        IReadOnlyList<TuneCandidate> candidates,
        string? client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(candidates);

        TunedMultiplex multiplex;
        ServiceFeed feed;
        TunerViewer viewer;
        bool shared;

        lock (_gate)
        {
            var running = _multiplexes.FirstOrDefault(candidate =>
                !candidate.IsFailed && candidates.Any(option => candidate.Serves(option.Device, option.Request)));

            if (running is not null)
            {
                multiplex = running;
                shared = true;
            }
            else
            {
                var free = candidates.FirstOrDefault(option => HasFreeTuner(option.Device, Delivery(option)));
                GuideLease? preempted = null;

                if (free is null)
                {
                    // A viewer comes before the guide: a tuner the grabber holds is taken from it.
                    foreach (var option in candidates)
                    {
                        preempted = _guideLeases.FirstOrDefault(lease => SameDevice(lease.Device, option.Device));

                        if (preempted is not null)
                        {
                            _guideLeases.Remove(preempted);

                            if (HasFreeTuner(option.Device, Delivery(option)))
                            {
                                free = option;
                                break;
                            }

                            _guideLeases.Add(preempted);
                            preempted = null;
                        }
                    }
                }

                Task? replaced = null;

                if (free is null)
                {
                    (free, replaced) = TakeOwnTuner(client, channel, candidates);
                }

                if (free is null)
                {
                    throw NoFreeTuner(channel, candidates);
                }

                multiplex = new TunedMultiplex(
                    free.Device,
                    free.Request,
                    () => _transports.Create(free.Device, free.Request),
                    _descramblers,
                    _slots,
                    _timings,
                    _loggerFactory,
                    OnMultiplexFailed,
                    _guide,
                    replaced ?? preempted?.PreemptAsync());

                _multiplexes.Add(multiplex);
                shared = false;
            }

            feed = multiplex.Acquire(channel);
            viewer = new TunerViewer(client, multiplex);
            _viewers.Add(viewer);
        }

        LogOpening(channel.Name, multiplex.Device.Label, multiplex.Request.Describe(), shared);

        var opening = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await multiplex.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (TunerTransportException error)
        {
            await ReleaseAsync(viewer, feed).ConfigureAwait(false);
            throw DvbErrors.TuneFailed(channel.Name, multiplex.Device.Label, error.Message);
        }
        catch (OperationCanceledException) when (feed.Fault is not null)
        {
            // Replaced while it was being tuned: that, rather than a cancelled tune, is the news.
            await ReleaseAsync(viewer, feed).ConfigureAwait(false);
            throw feed.Fault;
        }
        catch
        {
            await ReleaseAsync(viewer, feed).ConfigureAwait(false);
            throw;
        }

        var tuned = opening.ElapsedMilliseconds;

        try
        {
            await feed.WaitReadyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await ReleaseAsync(viewer, feed).ConfigureAwait(false);
            throw;
        }

        var (firstPacket, programMap, ready) = feed.Timings;
        LogOpenedAfter(channel.Name, opening.ElapsedMilliseconds, tuned, firstPacket, programMap, ready);

        return new ServiceLease(this, viewer, feed);
    }

    /// <summary>
    /// Takes a tuner of <paramref name="device"/> for a scan, which tunes it itself. Counted like
    /// any other use, so a scan cannot take the tuner a viewer is on, nor a viewer the scan's.
    /// </summary>
    public IAsyncDisposable ReserveForScan(TunerDevice device, DeliverySystem delivery, string target)
    {
        lock (_gate)
        {
            if (!HasFreeTuner(device, delivery))
            {
                throw DvbErrors.NoFreeTuner(target, device.Capacity(delivery), Describe(device));
            }

            var lease = new ScanLease(this, device, delivery);
            _scans.Add(lease);

            return lease;
        }
    }

    /// <summary>
    /// Takes a tuner of <paramref name="device"/> for the guide grabber, where one is free.
    /// Never waits and never takes one somebody uses; and a viewer who needs it later takes it
    /// back at once, through the lease's token.
    /// </summary>
    public GuideLease? TryReserveForGuide(TunerDevice device, DeliverySystem delivery)
    {
        lock (_gate)
        {
            if (!HasFreeTuner(device, delivery))
            {
                return null;
            }

            var lease = new GuideLease(this, device, delivery);
            _guideLeases.Add(lease);

            return lease;
        }
    }

    /// <summary>Whether a transponder is tuned for somebody already, so its guide is being read anyway.</summary>
    public bool IsTuned(TunerDevice device, TuneRequest request)
    {
        lock (_gate)
        {
            return _multiplexes.Any(multiplex => !multiplex.IsFailed && multiplex.Serves(device, request));
        }
    }

    /// <summary>What keeps channels on a device from being descrambled, for the tuner status.</summary>
    public IReadOnlyList<string> Problems(TunerDevice device)
    {
        lock (_gate)
        {
            return [.. _multiplexes.Where(multiplex => SameDevice(multiplex.Device, device)).SelectMany(multiplex => multiplex.Problems)];
        }
    }

    /// <summary>What each device is doing, for the tuner status.</summary>
    public IReadOnlyList<(int Tuners, IReadOnlyList<string> Channels)> Usage(TunerDevice device)
    {
        lock (_gate)
        {
            var usage = _multiplexes
                .Where(multiplex => SameDevice(multiplex.Device, device) && !multiplex.IsFailed)
                .Select(multiplex => (1, multiplex.ChannelIds))
                .ToList();

            usage.AddRange(_scans.Where(scan => SameDevice(scan.Device, device)).Select(_ => (1, (IReadOnlyList<string>)[])));
            usage.AddRange(_guideLeases.Where(lease => SameDevice(lease.Device, device)).Select(_ => (1, (IReadOnlyList<string>)[])));

            return usage;
        }
    }

    public async ValueTask DisposeAsync()
    {
        TunedMultiplex[] multiplexes;

        lock (_gate)
        {
            multiplexes = [.. _multiplexes];
            _multiplexes.Clear();
        }

        foreach (var multiplex in multiplexes)
        {
            await multiplex.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal async ValueTask ReleaseAsync(TunerViewer viewer, ServiceFeed feed)
    {
        var multiplex = viewer.Multiplex;
        var last = false;
        ServiceFeed? gone;

        lock (_gate)
        {
            _viewers.Remove(viewer);
            gone = multiplex.Release(feed);

            if (multiplex.FeedCount == 0 && _multiplexes.Remove(multiplex))
            {
                last = true;
            }
        }

        if (gone is not null)
        {
            await gone.DisposeAsync().ConfigureAwait(false);
        }

        if (last)
        {
            await multiplex.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void ReleaseScan(ScanLease lease)
    {
        lock (_gate)
        {
            _scans.Remove(lease);
        }
    }

    internal void ReleaseGuide(GuideLease lease)
    {
        lock (_gate)
        {
            _guideLeases.Remove(lease);
        }
    }

    /// <summary>
    /// A tuner that failed is taken out of the pool at once, so the next tune starts afresh
    /// rather than joining it. Its viewers have their error; their leases dispose it.
    /// </summary>
    private void OnMultiplexFailed(TunedMultiplex multiplex)
    {
        bool removed;

        lock (_gate)
        {
            removed = _multiplexes.Remove(multiplex);
        }

        if (!removed)
        {
            return;
        }

        // Off the reading loop, which is the thread reporting the failure and the one the tuner
        // is given back on.
        _ = GiveBackAsync(multiplex);
    }

    /// <summary>
    /// Gives a tuner nobody is on any more back to the device. Nothing has to wait for this, so
    /// its failure is logged here or it would be lost.
    /// </summary>
    private Task GiveBackAsync(TunedMultiplex multiplex) => Task.Run(async () =>
    {
        try
        {
            await multiplex.DisposeAsync().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Giving a tuner back may not take the server down.
        catch (Exception error)
#pragma warning restore CA1031
        {
            LogReleaseFailed(error, multiplex.Device.Label, multiplex.Request.Describe());
        }
    });

    /// <summary>
    /// The tuner of a client's stream, for the channel that client asks for now: a player
    /// changing channel may open the new one before it has closed the old one. Only a client's
    /// single stream, alone on its tuner: two streams are somebody watching or recording both,
    /// and nobody else's picture ends because of this client. The caller holds the lock.
    /// </summary>
    /// <returns>Where to tune, and what finishes when the old tuner has been given back.</returns>
    private (TuneCandidate? Option, Task? Replaced) TakeOwnTuner(string? client, StoredChannel channel, IReadOnlyList<TuneCandidate> candidates)
    {
        if (client is null)
        {
            return (null, null);
        }

        var own = _viewers.Where(viewer => viewer.Client == client && !viewer.Multiplex.IsFailed).ToList();

        if (own is not [{ Multiplex: var multiplex }] || _viewers.Count(viewer => viewer.Multiplex == multiplex) != 1)
        {
            return (null, null);
        }

        _multiplexes.Remove(multiplex);

        var option = candidates.FirstOrDefault(candidate => SameDevice(candidate.Device, multiplex.Device) && HasFreeTuner(candidate.Device, Delivery(candidate)));

        if (option is null)
        {
            _multiplexes.Add(multiplex);

            return (null, null);
        }

        _viewers.RemoveAll(viewer => viewer.Multiplex == multiplex);
        LogReplacing(multiplex.Device.Label, multiplex.Request, channel.Name);
        multiplex.End(feed => DvbErrors.ReplacedByOwnStream(feed.Channel.Name, channel.Name));

        return (option, GiveBackAsync(multiplex));
    }

    private bool HasFreeTuner(TunerDevice device, DeliverySystem delivery)
    {
        var sameKind = _multiplexes.Count(multiplex => SameDevice(multiplex.Device, device) && multiplex.Request.Tuning.Delivery == delivery)
            + _scans.Count(scan => SameDevice(scan.Device, device) && scan.Delivery == delivery)
            + _guideLeases.Count(lease => SameDevice(lease.Device, device) && lease.Delivery == delivery);
        var total = _multiplexes.Count(multiplex => SameDevice(multiplex.Device, device))
            + _scans.Count(scan => SameDevice(scan.Device, device))
            + _guideLeases.Count(lease => SameDevice(lease.Device, device));

        return sameKind < device.Capacity(delivery) && total < device.TotalCapacity;
    }

    private TunerException NoFreeTuner(StoredChannel channel, IReadOnlyList<TuneCandidate> candidates)
    {
        var devices = candidates.Select(candidate => candidate.Device).DistinctBy(device => device.Id).ToList();
        var tuners = devices.Sum(device => device.Capacity(Delivery(candidates.First(candidate => candidate.Device == device))));

        return DvbErrors.NoFreeTuner(channel.Name, tuners, string.Join("; ", devices.Select(Describe)));
    }

    /// <summary>What a device's tuners are busy with, for an error message.</summary>
    private string Describe(TunerDevice device)
    {
        var busy = _multiplexes
            .Where(multiplex => SameDevice(multiplex.Device, device))
            .Select(multiplex => multiplex.Request.Describe())
            .Concat(_scans.Where(scan => SameDevice(scan.Device, device)).Select(_ => "a channel scan"))
            .Concat(_guideLeases.Where(lease => SameDevice(lease.Device, device)).Select(_ => "reading the guide"))
            .ToList();

        return busy.Count == 0
            ? $"{device.Label}: no tuner for this signal"
            : $"{device.Label}: {string.Join(", ", busy)}";
    }

    private static DeliverySystem Delivery(TuneCandidate candidate) => candidate.Request.Tuning.Delivery;

    private static bool SameDevice(TunerDevice left, TunerDevice right) =>
        string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase);

    [LoggerMessage(EventId = 9886, Level = LogLevel.Information, Message = "'{Channel}' was ready after {Total} ms: the tuner was tuned after {Tuned} ms; from the feed's start, the first packet came after {FirstPacket} ms, the program map after {ProgramMap} ms, and it was ready - keys included - after {Ready} ms.")]
    private partial void LogOpenedAfter(string channel, long total, long tuned, long firstPacket, long programMap, long ready);

    [LoggerMessage(EventId = 9850, Level = LogLevel.Information, Message = "Opening '{Channel}' on {Device}, {Transponder} ({Shared}).")]
    private partial void LogOpeningCore(string channel, string device, string transponder, string shared);

    [LoggerMessage(EventId = 9851, Level = LogLevel.Warning, Message = "Giving the tuner of {Device} on {Transponder} back after it failed did not finish cleanly; the device may hold the session until it times out.")]
    private partial void LogReleaseFailed(Exception error, string device, string transponder);

    [LoggerMessage(EventId = 9852, Level = LogLevel.Information, Message = "The client's stream on {Device}, {Transponder} gives way to '{Channel}': no other tuner is free.")]
    private partial void LogReplacing(string device, TuneRequest transponder, string channel);

    private void LogOpening(string channel, string device, string transponder, bool shared) =>
        LogOpeningCore(channel, device, transponder, shared ? "sharing a tuned transponder" : "new tuner");

    /// <summary>A tuner held for a scan.</summary>
    private sealed class ScanLease(TunerPool pool, TunerDevice device, DeliverySystem delivery) : IAsyncDisposable
    {
        private int _released;

        public TunerDevice Device { get; } = device;

        public DeliverySystem Delivery { get; } = delivery;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                pool.ReleaseScan(this);
            }

            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Somebody on a tuner. <see cref="Client"/> is null for a stream that nothing may replace.</summary>
internal sealed class TunerViewer(string? client, TunedMultiplex multiplex)
{
    public string? Client { get; } = client;

    public TunedMultiplex Multiplex { get; } = multiplex;
}

/// <summary>One viewer's hold on a tuned service. Disposing it gives the viewer back, and the tuner with the last one.</summary>
internal sealed class ServiceLease : IAsyncDisposable
{
    private readonly TunerPool _pool;
    private readonly TunerViewer _viewer;
    private int _released;

    public ServiceLease(TunerPool pool, TunerViewer viewer, ServiceFeed feed)
    {
        _pool = pool;
        _viewer = viewer;
        Feed = feed;
    }

    public TunedMultiplex Multiplex => _viewer.Multiplex;

    /// <summary>The pool the tuner is from, for how many of the device's tuners are in use.</summary>
    public TunerPool Pool => _pool;

    public ServiceFeed Feed { get; }

    public bool IsReleased => Volatile.Read(ref _released) != 0;

    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _released, 1) == 0 ? _pool.ReleaseAsync(_viewer, Feed) : ValueTask.CompletedTask;
}

/// <summary>
/// A tuner lent to the guide grabber. Taken back the moment a viewer needs it: the token is
/// cancelled, and the viewer's tune waits until the grabber has let go of the tuner - the
/// device would otherwise see one session too many.
/// </summary>
internal sealed class GuideLease : IAsyncDisposable
{
    private static readonly TimeSpan ReleaseWait = TimeSpan.FromSeconds(5);

    private readonly TunerPool _pool;
    private readonly CancellationTokenSource _preempt = new();
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public GuideLease(TunerPool pool, TunerDevice device, DeliverySystem delivery)
    {
        _pool = pool;
        Device = device;
        Delivery = delivery;
    }

    public TunerDevice Device { get; }

    public DeliverySystem Delivery { get; }

    /// <summary>Cancelled when a viewer takes the tuner.</summary>
    public CancellationToken Token => _preempt.Token;

    public bool WasPreempted => _preempt.IsCancellationRequested;

    /// <summary>Tells the grabber to stop and waits, briefly, until it has.</summary>
    public async Task PreemptAsync()
    {
        await _preempt.CancelAsync().ConfigureAwait(false);
        await _released.Task.WaitAsync(ReleaseWait).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public ValueTask DisposeAsync()
    {
        if (_released.TrySetResult())
        {
            _pool.ReleaseGuide(this);
        }

        // One lease per transponder per guide run, each with a token a dwell links itself to.
        _preempt.Dispose();

        return ValueTask.CompletedTask;
    }
}
