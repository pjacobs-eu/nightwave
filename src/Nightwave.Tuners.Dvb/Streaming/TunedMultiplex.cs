using System.Threading.Channels;
using Nightwave.Tuners.Dvb.Channels;
using Nightwave.Tuners.Dvb.Descrambling;
using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.Epg;
using Nightwave.Tuners.Dvb.Mpeg.Si;
using Nightwave.Tuners.Dvb.Mpeg;
using Nightwave.Tuners.Dvb.Transport;
using Microsoft.Extensions.Logging;

namespace Nightwave.Tuners.Dvb.Streaming;

/// <summary>
/// One tuner in use: a transponder and the services being watched from it. The pids the tuner passes are the union of what those services need, and
/// change as they come and go; one loop reads the tuner and hands every packet to each of them.
/// </summary>
/// <remarks>
/// A tuner that is lost - the server closed the connection, ended the stream, refused a change
/// of pids, or over UDP simply stopped sending - is tuned again rather than given up: a picture
/// that freezes for a few seconds and comes back is what somebody watching expects of a dish in
/// the rain, and a player told the channel is gone is not. Its services stay as they are and
/// keep their readers fed with a PAT meanwhile. Only a tuner that cannot be had back within
/// <see cref="FeedTimings.Recovery"/> ends its channels.
/// </remarks>
internal sealed partial class TunedMultiplex : IAsyncDisposable
{
    private const int ReadSize = 128 * 1024;

    /// <summary>About 350 packets: what the descrambler is fed at once, at the least.</summary>
    private const int BatchSize = 348 * TransportPacket.Size;

    private static readonly TimeSpan BatchLatency = TimeSpan.FromMilliseconds(100);

    private readonly Func<ITsTransport> _createTransport;
    private readonly IServiceDescramblerFactory _descramblers;
    private readonly DescramblerSlots _slots;
    private readonly FeedTimings _timings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly Action<TunedMultiplex> _onFailed;
    private readonly Dictionary<string, ServiceFeed> _feeds = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _pidUpdates = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _beforeStart;
    private readonly EitCollector? _guide;

    private ITsTransport _transport;
    private ServiceFeed[] _snapshot = [];
    private CancellationTokenSource? _session;
    private volatile string? _lostBecause;
    private Task? _started;
    private Task? _loop;
    private HashSet<int> _applied = [];
    private int _pidsDirty;
    private int _disposed;
    private volatile bool _failed;
    private volatile bool _running;

    public TunedMultiplex(
        TunerDevice device,
        TuneRequest request,
        Func<ITsTransport> createTransport,
        IServiceDescramblerFactory descramblers,
        DescramblerSlots slots,
        FeedTimings timings,
        ILoggerFactory loggerFactory,
        Action<TunedMultiplex> onFailed,
        EpgService? guide = null,
        Task? beforeStart = null)
    {
        Device = device;
        Request = request;
        _createTransport = createTransport;
        _transport = createTransport();
        _descramblers = descramblers;
        _slots = slots;
        _timings = timings;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<TunedMultiplex>();
        _onFailed = onFailed;
        _beforeStart = beforeStart ?? Task.CompletedTask;

        // The guide comes along for free on a SAT>IP tuner: one more pid.
        _guide = guide is { IsEnabled: true } ? guide.CreateCollector() : null;
    }

    public TunerDevice Device { get; }

    public TuneRequest Request { get; }

    /// <summary>What the tuner reports about its signal and what the network lost, right now.</summary>
    public TransportStatus TransportStatus => _transport.Status;

    /// <summary>The channels watched from this tuner, for the details of each of them.</summary>
    public IReadOnlyList<StoredChannel> Channels
    {
        get
        {
            lock (_gate)
            {
                return [.. _feeds.Values.Select(feed => feed.Channel)];
            }
        }
    }

    /// <summary>True once the tuner failed; nothing new is added to a multiplex in that state.</summary>
    public bool IsFailed => _failed;

    /// <summary>Channels watched from this tuner, for the tuner status.</summary>
    public IReadOnlyList<string> ChannelIds
    {
        get
        {
            lock (_gate)
            {
                return [.. _feeds.Keys];
            }
        }
    }

    /// <summary>What keeps services on this tuner from being descrambled, one line per service.</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            lock (_gate)
            {
                return
                [
                    .. _feeds.Values
                        .Where(feed => feed.DescramblingProblem is not null)
                        .Select(feed => $"{feed.Channel.Name}: {feed.DescramblingProblem!.Message}"),
                ];
            }
        }
    }

    public int FeedCount
    {
        get
        {
            lock (_gate)
            {
                return _feeds.Count;
            }
        }
    }

    /// <summary>Whether a channel tuned with <paramref name="request"/> on <paramref name="device"/> can be served from here.</summary>
    public bool Serves(TunerDevice device, TuneRequest request)
    {
        if (!string.Equals(device.Id, Device.Id, StringComparison.OrdinalIgnoreCase) || request.Source != Request.Source)
        {
            return false;
        }

        return Request.Tuning.IsSameTransponder(request.Tuning);
    }

    /// <summary>The feed of a channel, created where it is the first viewer. The caller holds the pool's lock.</summary>
    public ServiceFeed Acquire(StoredChannel channel)
    {
        lock (_gate)
        {
            if (!_feeds.TryGetValue(channel.Id, out var feed) || feed.IsFaulted)
            {
                feed = new ServiceFeed(
                    channel,
                    Device,
                    Request.Describe(),
                    _descramblers,
                    _slots,
                    PidsChanged,
                    _timings,
                    _loggerFactory.CreateLogger<ServiceFeed>());

                _feeds[channel.Id] = feed;
                _snapshot = [.. _feeds.Values];

                // A service joining a running tuner needs its pids added.
                PidsChanged();
            }

            feed.References++;

            return feed;
        }
    }

    /// <summary>
    /// Gives a viewer back. Returns the feed when that was its last viewer, so the caller can
    /// dispose it outside its lock.
    /// </summary>
    public ServiceFeed? Release(ServiceFeed feed)
    {
        lock (_gate)
        {
            feed.References--;

            if (feed.References > 0)
            {
                return null;
            }

            if (_feeds.TryGetValue(feed.Channel.Id, out var current) && ReferenceEquals(current, feed))
            {
                _feeds.Remove(feed.Channel.Id);
                _snapshot = [.. _feeds.Values];
            }
        }

        PidsChanged();

        return feed;
    }

    /// <summary>
    /// Ends every service on this tuner with an error of the caller's. For a tuner the pool has
    /// already taken out and gives back itself.
    /// </summary>
    public void End(Func<ServiceFeed, Exception> error)
    {
        _failed = true;

        foreach (var feed in Volatile.Read(ref _snapshot))
        {
            feed.Fail(error(feed));
        }
    }

    /// <summary>Tunes, once; every caller waits for the same attempt.</summary>
    public Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _started ??= Task.Run(StartAsync, CancellationToken.None);
        }

        return _started.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);

        // The tune first: it is what starts the reading loop, so waiting for the loop before it
        // would miss a loop that was started while this was waiting - and leave it reading a
        // transport that is about to be disposed.
        if (_started is not null)
        {
            await _started.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        if (_loop is not null)
        {
            await _loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        ServiceFeed[] feeds;
        lock (_gate)
        {
            feeds = [.. _feeds.Values];
            _feeds.Clear();
            _snapshot = [];
        }

        foreach (var feed in feeds)
        {
            await feed.DisposeAsync().ConfigureAwait(false);
        }

        await _transport.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
        _pidUpdates.Dispose();

        LogReleased(Device.Label, Request);
    }

    private async Task StartAsync()
    {
        // A tuner taken from the guide grabber is only used once the grabber has let go of it.
        await _beforeStart.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        var pids = DesiredPids();

        await _transport.StartAsync(pids, _stop.Token).ConfigureAwait(false);
        _applied = [.. pids];
        _running = true;

        _loop = Task.Run(ReadLoopAsync, CancellationToken.None);

        // Services may have changed their minds while the tuner was being tuned.
        PidsChanged();
    }

    private HashSet<int> DesiredPids()
    {
        var pids = new HashSet<int> { TransportPacket.PatPid };

        if (_guide is not null)
        {
            pids.Add(EventInformationTable.Pid);
        }

        foreach (var feed in Volatile.Read(ref _snapshot))
        {
            pids.UnionWith(feed.WantedPids);
        }

        return pids;
    }

    /// <summary>
    /// Brings the tuner's pids in line with what the services want. Never on the reading loop:
    /// changing pids is a round trip to the device, and the loop may not wait for one.
    /// </summary>
    private void PidsChanged()
    {
        if (!_running || _failed || _stop.IsCancellationRequested)
        {
            return;
        }

        Interlocked.Exchange(ref _pidsDirty, 1);
        _ = Task.Run(ApplyPidsAsync, CancellationToken.None);
    }

    private async Task ApplyPidsAsync()
    {
        try
        {
            await _pidUpdates.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            // Whoever gets here first applies the latest wish of everybody who asked meanwhile.
            while (Interlocked.Exchange(ref _pidsDirty, 0) == 1 && !_stop.IsCancellationRequested)
            {
                var wanted = DesiredPids();
                if (wanted.SetEquals(_applied))
                {
                    continue;
                }

                await _transport.SetPidsAsync(wanted, _stop.Token).ConfigureAwait(false);
                _applied = wanted;
                LogPidsChanged(Device.Label, Request, new PidList(wanted));
            }
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
        {
            // Stopping. The last viewer leaving both asks for a change of pids and disposes the
            // multiplex, so this can reach a transport that has just been disposed; that change
            // is moot either way.
        }
        catch (TunerTransportException error)
        {
            // Tuned again with the whole list, which is also what a server that is still there
            // makes of a change it did not take.
            Lose(error.Message);
        }
        finally
        {
            try
            {
                _pidUpdates.Release();
            }
            catch (ObjectDisposedException)
            {
                // Disposed while this was running.
            }
        }
    }

    /// <summary>Reads the tuner for as long as it is wanted, tuning it again whenever it is lost.</summary>
    private async Task ReadLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var lost = await ReadUntilLostAsync().ConfigureAwait(false);

            if (lost is null)
            {
                return;
            }

            if (!await RecoverAsync(lost).ConfigureAwait(false))
            {
                if (!_stop.IsCancellationRequested)
                {
                    Fail(lost);
                }

                return;
            }
        }
    }

    /// <summary>Asks the reading loop to tune again. From anywhere; the loop does the work.</summary>
    private void Lose(string reason)
    {
        _lostBecause = reason;

        try
        {
            Volatile.Read(ref _session)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // That session has already ended.
        }
    }

    /// <summary>
    /// Reads the tuner and hands packets to the services in batches: at least
    /// <see cref="BatchSize"/> bytes, or what arrived within <see cref="BatchLatency"/>. A
    /// descrambler is several times faster on a few hundred packets than on the seven of one RTP
    /// datagram, and a radio service at a few hundred kbit/s still gets its packets on time.
    /// </summary>
    /// <returns>Why the tuner was lost, or null when it was released.</returns>
    private async Task<string?> ReadUntilLostAsync()
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        Volatile.Write(ref _session, session);
        _lostBecause = null;

        var incoming = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        var pump = Task.Run(() => PumpAsync(_transport, incoming.Writer, session.Token), CancellationToken.None);
        var batch = new byte[BatchSize + ReadSize + TransportPacket.Size];

        try
        {
            while (true)
            {
                // Silence is how a server over UDP says it has dropped the session.
                using (var quiet = CancellationTokenSource.CreateLinkedTokenSource(session.Token))
                {
                    quiet.CancelAfter(_timings.Stall);

                    try
                    {
                        if (!await incoming.Reader.WaitToReadAsync(quiet.Token).ConfigureAwait(false))
                        {
                            return _stop.IsCancellationRequested ? null : $"{Device.Label} ended the stream of {Request.Describe()}.";
                        }
                    }
                    catch (OperationCanceledException) when (!session.IsCancellationRequested)
                    {
                        return $"{Device.Label} sent nothing on {Request.Describe()} for {_timings.Stall.TotalSeconds:0} seconds.";
                    }
                }

                var length = 0;
                using var window = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                window.CancelAfter(BatchLatency);

                while (length < BatchSize)
                {
                    while (length < BatchSize && incoming.Reader.TryRead(out var chunk))
                    {
                        chunk.CopyTo(batch, length);
                        length += chunk.Length;
                    }

                    if (length >= BatchSize)
                    {
                        break;
                    }

                    try
                    {
                        if (!await incoming.Reader.WaitToReadAsync(window.Token).ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (!session.IsCancellationRequested)
                    {
                        // The batch has waited long enough.
                        break;
                    }
                }

                if (length == 0)
                {
                    continue;
                }

                foreach (var feed in Volatile.Read(ref _snapshot))
                {
                    feed.Process(batch.AsSpan(0, length));
                }

                _guide?.Process(batch.AsSpan(0, length));
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            return _stop.IsCancellationRequested ? null : _lostBecause ?? $"{Device.Label} lost {Request.Describe()}.";
        }
        catch (TunerTransportException error)
        {
            return error.Message;
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            await pump.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            Volatile.Write(ref _session, null);
        }
    }

    /// <summary>
    /// Tunes the transponder again after it was lost, with every pid its services want now. Keeps
    /// their readers fed with a PAT while it tries.
    /// </summary>
    /// <returns>Whether the tuner is back.</returns>
    private async Task<bool> RecoverAsync(string reason)
    {
        LogLost(Device.Label, Request, reason);

        try
        {
            // No change of pids goes to a transport that is being replaced.
            await _pidUpdates.WaitAsync(_stop.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }

        var lost = System.Diagnostics.Stopwatch.StartNew();
        var backoff = _timings.RecoveryBackoff;
        var lastReason = reason;

        try
        {
            // Not running from here until a new transport is in: the old one is about to be
            // disposed, and a change of pids asked for meanwhile would go to it.
            _running = false;
            await _transport.DisposeAsync().ConfigureAwait(false);

            for (var attempt = 0; !_stop.IsCancellationRequested; attempt++)
            {
                var wait = backoff.Count == 0 ? TimeSpan.Zero : backoff[Math.Min(attempt, backoff.Count - 1)];

                if (lost.Elapsed + wait > _timings.Recovery)
                {
                    LogGivenUp(Device.Label, Request, lost.Elapsed.TotalSeconds, lastReason);
                    return false;
                }

                await KeepAliveAsync(wait).ConfigureAwait(false);

                var transport = _createTransport();
                var pids = DesiredPids();

                try
                {
                    await transport.StartAsync(pids, _stop.Token).ConfigureAwait(false);
                }
                catch (TunerTransportException error)
                {
                    lastReason = error.Message;
                    await transport.DisposeAsync().ConfigureAwait(false);
                    LogRecoveryFailed(Device.Label, Request, attempt + 1, error.Message);
                    continue;
                }

                _transport = transport;
                _applied = pids;
                _running = true;
                LogRecovered(Device.Label, Request, lost.Elapsed.TotalSeconds);

                return true;
            }

            return false;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            try
            {
                _pidUpdates.Release();
            }
            catch (ObjectDisposedException)
            {
                // Released meanwhile.
            }

            // Services may have come or gone while the tuner was away.
            PidsChanged();
        }
    }

    /// <summary>Waits, sending every service's PAT once a second meanwhile.</summary>
    private async Task KeepAliveAsync(TimeSpan wait)
    {
        var until = DateTime.UtcNow + wait;

        do
        {
            foreach (var feed in Volatile.Read(ref _snapshot))
            {
                feed.KeepAlive();
            }

            var left = until - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1), _stop.Token).ConfigureAwait(false);
        }
        while (DateTime.UtcNow < until);
    }

    /// <summary>Reads the tuner as fast as it delivers and cuts what it reads into whole packets.</summary>
    private async Task PumpAsync(ITsTransport transport, ChannelWriter<byte[]> incoming, CancellationToken cancellationToken)
    {
        var input = new byte[ReadSize];
        var packets = new byte[ReadSize + TransportPacket.Size];
        var aligner = new PacketAligner();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await transport.ReadAsync(input, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new TunerTransportException($"{Device.Label} ended the stream of {Request.Describe()}.");
                }

                var count = aligner.Push(input.AsSpan(0, read), packets);
                if (count > 0)
                {
                    await incoming.WriteAsync(packets.AsSpan(0, count).ToArray(), cancellationToken).ConfigureAwait(false);
                }
            }

            incoming.TryComplete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            incoming.TryComplete();
        }
        catch (TunerTransportException error)
        {
            incoming.TryComplete(error);
        }
        catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or ObjectDisposedException)
        {
            incoming.TryComplete(new TunerTransportException($"Reading from {Device.Label} failed: {error.Message}", error));
        }
    }

    private void Fail(string reason)
    {
        if (_failed)
        {
            return;
        }

        _failed = true;
        LogFailed(Device.Label, Request.Describe(), reason);

        foreach (var feed in Volatile.Read(ref _snapshot))
        {
            feed.Fail(DvbErrors.StreamLost(feed.Channel.Name, reason));
        }

        _onFailed(this);
    }

    [LoggerMessage(EventId = 9840, Level = LogLevel.Debug, Message = "{Device} on {Transponder} now passes pids {Pids}.")]
    private partial void LogPidsChanged(string device, TuneRequest transponder, PidList pids);

    [LoggerMessage(EventId = 9841, Level = LogLevel.Warning, Message = "The tuner of {Device} on {Transponder} stopped: {Reason}")]
    private partial void LogFailed(string device, string transponder, string reason);

    [LoggerMessage(EventId = 9843, Level = LogLevel.Warning, Message = "The tuner of {Device} on {Transponder} was lost ({Reason}); tuning it again, its channels keep running.")]
    private partial void LogLost(string device, TuneRequest transponder, string reason);

    [LoggerMessage(EventId = 9844, Level = LogLevel.Information, Message = "Attempt {Attempt} to tune {Device} to {Transponder} again failed: {Reason}")]
    private partial void LogRecoveryFailed(string device, TuneRequest transponder, int attempt, string reason);

    [LoggerMessage(EventId = 9845, Level = LogLevel.Information, Message = "The tuner of {Device} is back on {Transponder} after {Seconds:0.0} s.")]
    private partial void LogRecovered(string device, TuneRequest transponder, double seconds);

    [LoggerMessage(EventId = 9846, Level = LogLevel.Warning, Message = "Gave up tuning {Device} to {Transponder} again after {Seconds:0} s: {Reason}")]
    private partial void LogGivenUp(string device, TuneRequest transponder, double seconds, string reason);

    [LoggerMessage(EventId = 9842, Level = LogLevel.Information, Message = "Released the tuner of {Device} on {Transponder}.")]
    private partial void LogReleased(string device, TuneRequest transponder);
}
