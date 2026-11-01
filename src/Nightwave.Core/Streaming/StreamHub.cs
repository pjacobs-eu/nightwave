using Microsoft.Extensions.Logging;
using Nightwave.Core.Analysis;
using Nightwave.Core.Configuration;
using Nightwave.Core.Lineup;
using Nightwave.Core.Tuning;

namespace Nightwave.Core.Streaming;

/// <summary>One client receiving a channel, as the dashboard shows it.</summary>
public sealed class StreamSession
{
    public required Guid Id { get; init; }

    public required ChannelKey Channel { get; init; }

    public required string ChannelName { get; init; }

    public required string Client { get; init; }

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    public ITunedStream? Stream { get; init; }

    public TsRepairFilter? Repair { get; init; }

    /// <summary>Measures what the source delivers, before any repair.</summary>
    public TsAnalyzer Analyzer { get; } = new();

    /// <summary>Bridges outages with the last picture. Null where the tuner has the repair off.</summary>
    public StreamBridge? Bridge { get; set; }

    /// <summary>Ends a measurement without a player; null for a stream somebody watches.</summary>
    public CancellationTokenSource? StopMeasurement { get; set; }

    /// <summary>What the tuner knows about this stream, where it knows anything.</summary>
    public ITunedStreamTelemetry? Telemetry => Stream as ITunedStreamTelemetry;

    /// <summary>The channel as the lineup has it.</summary>
    public TvChannel? Details { get; init; }

    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>The last samples, oldest first.</summary>
    public IReadOnlyList<TelemetrySample> History
    {
        get
        {
            lock (_samples)
            {
                return [.. _samples];
            }
        }
    }

    /// <summary>Bits per second of each pid over the last sample.</summary>
    public IReadOnlyDictionary<int, double> PidBitRates => _pidRates;

    public void CountSent(long bytes) => Interlocked.Add(ref _bytesSent, bytes);

    /// <summary>What the stream's counters stood at last time, for the log.</summary>
    public StreamCounters? Counters { get; set; }

    /// <summary>Takes one sample. Called about once a second.</summary>
    public void Sample(DateTimeOffset now)
    {
        var analysis = Analyzer.Snapshot();

        if (Bridge is not null && analysis.PmtPid is { } map)
        {
            Bridge.ProgramMapPid = map;
        }

        var seconds = _lastSampleAt is { } last ? (now - last).TotalSeconds : 0;

        if (seconds > 0)
        {
            var rates = new Dictionary<int, double>(analysis.Pids.Count);

            foreach (var pid in analysis.Pids)
            {
                rates[pid.Pid] = (pid.Packets - _lastPidPackets.GetValueOrDefault(pid.Pid)) * 188 * 8 / seconds;
            }

            _pidRates = rates;

            var signal = Telemetry?.Signal;
            var sample = new TelemetrySample(now, (analysis.Bytes - _lastBytes) * 8 / seconds / 1_000_000, signal?.Level, signal?.Quality, Telemetry?.Descrambling?.EcmTimeMs);

            lock (_samples)
            {
                _samples.Enqueue(sample);

                while (_samples.Count > HistoryLength)
                {
                    _samples.Dequeue();
                }
            }
        }

        _lastSampleAt = now;
        _lastBytes = analysis.Bytes;
        _lastPidPackets = analysis.Pids.ToDictionary(pid => pid.Pid, pid => pid.Packets);
    }

    private const int HistoryLength = 300;

    private readonly Queue<TelemetrySample> _samples = new();
    private long _bytesSent;
    private DateTimeOffset? _lastSampleAt;
    private long _lastBytes;
    private Dictionary<int, long> _lastPidPackets = [];
    private volatile IReadOnlyDictionary<int, double> _pidRates = new Dictionary<int, double>();
}

/// <summary>The counters of a stream at one sample, to see what changed since the last one.</summary>
public sealed record StreamCounters(long Continuity, long Transport, long Sync, long Pcr, long Datagrams, long NoKey, long Held, long Outages, bool Frozen, bool Locked, DescramblingState? Descrambling);

/// <summary>One second of a stream: bit rate in Mbit/s, level and quality between 0 and 1, the last ECM time.</summary>
public sealed record TelemetrySample(DateTimeOffset At, double BitRateMbps, double? Level, double? Quality, long? EcmTimeMs = null);

/// <summary>What the HTTP endpoint gets for a channel: bytes to copy, or an address to send the client to.</summary>
public sealed record OpenedChannel(StreamSession Session, Stream? Content, Uri? Redirect, Func<ValueTask> Close);

/// <summary>
/// Opens channels for clients and keeps track of who receives what. Bytes go out as the source
/// delivered them: never decoded, never transcoded, never deinterlaced. The only thing that may
/// touch them on the way is the packet-level repair, where a tuner has it switched on.
/// </summary>
public sealed partial class StreamHub
{
    private static readonly TimeSpan MeasurementLimit = TimeSpan.FromMinutes(5);

    private readonly LineupService _lineup;
    private readonly ConfigStore _config;
    private readonly ILogger<StreamHub> _logger;
    private readonly Lock _gate = new();
    private readonly List<StreamSession> _sessions = [];

    public StreamHub(LineupService lineup, ConfigStore config, ILogger<StreamHub> logger)
    {
        _lineup = lineup;
        _config = config;
        _logger = logger;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<StreamSession> Sessions
    {
        get
        {
            lock (_gate)
            {
                return [.. _sessions];
            }
        }
    }

    /// <inheritdoc cref="OpenAsync(ChannelKey, string, bool, CancellationToken)"/>
    public Task<OpenedChannel> OpenAsync(ChannelKey key, string client, CancellationToken cancellationToken) =>
        OpenAsync(key, client, false, cancellationToken);

    /// <summary>Tunes a channel for a client. Fails loudly with what stopped it; it never waits for a tuner.</summary>
    /// <param name="key">The channel.</param>
    /// <param name="client">Who it is for.</param>
    /// <param name="replaceOwnStream">Whether it may replace the client's one running stream when nothing else is free.</param>
    /// <param name="cancellationToken">Ends the wait for the channel.</param>
    public async Task<OpenedChannel> OpenAsync(ChannelKey key, string client, bool replaceOwnStream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);

        var provider = _lineup.Provider(key.ProviderId)
            ?? throw new TunerException(key.ProviderId, "tuner.provider_missing", $"There is no tuner of kind '{key.ProviderId}'.", "Copy the playlist's address again from the bouquet page.");

        var stream = await provider.OpenAsync(new TunedStreamRequest { ChannelId = key.ChannelId, ClientDeviceId = client, ReplacesOwnStream = replaceOwnStream }, cancellationToken).ConfigureAwait(false);

        try
        {
            var channels = await provider.GetChannelsAsync(cancellationToken).ConfigureAwait(false);
            var details = channels.FirstOrDefault(channel => string.Equals(channel.Id, key.ChannelId, StringComparison.OrdinalIgnoreCase));
            var name = details?.Name ?? key.ChannelId;

            if (stream.DirectAddress is { } direct)
            {
                await stream.DisposeAsync().ConfigureAwait(false);

                return new OpenedChannel(new StreamSession { Id = stream.Id, Channel = key, ChannelName = name, Client = client }, null, direct, () => ValueTask.CompletedTask);
            }

            var repair = ShouldRepair(key) ? new TsRepairFilter() : null;
            var session = new StreamSession { Id = stream.Id, Channel = key, ChannelName = name, Client = client, Stream = stream, Repair = repair, Details = details };

            if (repair is not null)
            {
                session.Bridge = new StreamBridge(pid => CodecOf(session, pid));
            }

            Stream content = new AnalyzingStream(await stream.OpenReadAsync(cancellationToken).ConfigureAwait(false), session.Analyzer);

            if (repair is not null)
            {
                // The repair holds a picture back until it can start cleanly; the bridge keeps
                // the last one on screen and the player supplied in the meantime.
                content = new BridgingStream(new RepairingStream(content, repair), session.Bridge!);
            }

            lock (_gate)
            {
                _sessions.Add(session);
            }

            LogOpened(name, client, repair is not null);
            Changed?.Invoke(this, EventArgs.Empty);

            return new OpenedChannel(session, content, null, () => CloseAsync(session, content));
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);

            throw;
        }
    }

    /// <summary>
    /// Whether a channel's tuner has the repair on. A web tuner's id is the first part of its
    /// channel ids; SAT>IP channels belong to the satellite rather than to one device, so there
    /// any SAT>IP tuner asking for it decides.
    /// </summary>
    private bool ShouldRepair(ChannelKey key)
    {
        var tuners = _config.Current.Tuners;
        var separator = key.ChannelId.IndexOf(':', StringComparison.Ordinal);
        var tunerId = separator > 0 ? key.ChannelId[..separator] : null;

        return tuners.Any(tuner => tuner.SupportsRepair && tuner.Repair && tuner.Id == tunerId)
            || (string.Equals(key.ProviderId, "satip", StringComparison.OrdinalIgnoreCase)
                && tuners.Any(tuner => tuner is { SupportsRepair: true, Repair: true }));
    }

    /// <summary>
    /// Tunes a channel with no player attached, to look at it on the monitor. Holds a tuner like
    /// any viewer, so it ends by itself after a few minutes.
    /// </summary>
    public async Task<StreamSession> MeasureAsync(ChannelKey key, CancellationToken cancellationToken)
    {
        var opened = await OpenAsync(key, "Nightwave", cancellationToken).ConfigureAwait(false);

        if (opened.Content is not { } content)
        {
            throw new TunerException(key.ProviderId, "tuner.not_measurable", $"'{opened.Session.ChannelName}' is not delivered through this server, so it cannot be measured.", "Open it in a player; the player fetches it from its own address.");
        }

        var stop = new CancellationTokenSource(MeasurementLimit);

        opened.Session.StopMeasurement = stop;

        _ = Task.Run(
            async () =>
            {
                var buffer = new byte[188 * 348];

                try
                {
                    while (await content.ReadAsync(buffer, stop.Token).ConfigureAwait(false) > 0)
                    {
                    }
                }
                catch (Exception error) when (error is OperationCanceledException or NightwaveException or IOException)
                {
                    // Stopped, or the tuner went away; the monitor shows the stream as ended.
                }
                finally
                {
                    await opened.Close().ConfigureAwait(false);
                    stop.Dispose();
                }
            },
            CancellationToken.None);

        return opened.Session;
    }

    /// <summary>Takes a sample of every running stream. Called about once a second.</summary>
    public void Sample()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var session in Sessions)
        {
            session.Sample(now);
            ReportChanges(session);
        }
    }

    /// <summary>Puts every new disturbance of a stream in the log, as it happens.</summary>
    private void ReportChanges(StreamSession session)
    {
        var analysis = session.Analyzer.Snapshot();
        var signal = session.Telemetry?.Signal;
        var transport = session.Telemetry?.Transport;
        var descrambling = session.Telemetry?.Descrambling;
        var repair = session.Repair?.Stats;
        var bridge = session.Bridge?.Stats;

        var now = new StreamCounters(
            analysis.ContinuityErrors,
            analysis.TransportErrors,
            analysis.SyncLossBytes,
            analysis.PcrIntervalErrors,
            transport?.DatagramsLost ?? 0,
            descrambling?.StillScrambledPackets ?? 0,
            repair?.PacketsHeldBack ?? 0,
            bridge?.Outages ?? 0,
            bridge?.Active ?? false,
            signal?.Locked ?? true,
            descrambling?.State);

        var was = session.Counters;

        session.Counters = now;

        if (was is not null && now != was)
        {
            LogDisturbance(
                session.ChannelName,
                now.Continuity - was.Continuity,
                now.Transport - was.Transport,
                now.Sync - was.Sync,
                now.Pcr - was.Pcr,
                now.Datagrams - was.Datagrams,
                now.NoKey - was.NoKey,
                now.Held - was.Held,
                now.Outages - was.Outages,
                now.Frozen,
                now.Locked,
                now.Descrambling?.ToString() ?? "-",
                signal?.LevelRaw ?? "-",
                signal?.QualityRaw ?? "-");
        }
    }

    public StreamSession? Find(Guid id) => Sessions.FirstOrDefault(session => session.Id == id);

    /// <summary>What the program map says about a pid.</summary>
    private static string? CodecOf(StreamSession session, int pid) =>
        session.Analyzer.Snapshot().Pids.FirstOrDefault(entry => entry.Pid == pid)?.Codec;

    private async ValueTask CloseAsync(StreamSession session, Stream content)
    {
        lock (_gate)
        {
            _sessions.Remove(session);
        }

        await content.DisposeAsync().ConfigureAwait(false);
        await session.Stream!.DisposeAsync().ConfigureAwait(false);

        var analysis = session.Analyzer.Snapshot();
        var bridge = session.Bridge?.Stats;

        LogClosed(session.ChannelName, session.Client, session.BytesSent);
        LogSummary(
            session.ChannelName,
            (long)(DateTimeOffset.UtcNow - session.StartedAt).TotalSeconds,
            analysis.ContinuityErrors,
            analysis.TransportErrors,
            analysis.SyncLossBytes,
            session.Telemetry?.Transport?.DatagramsLost ?? 0,
            session.Repair?.Stats.GapsFound ?? 0,
            bridge?.Outages ?? 0,
            bridge?.FrozenMilliseconds ?? 0);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    [LoggerMessage(EventId = 122, Level = LogLevel.Warning, Message = "Disturbance on '{Channel}': continuity {Continuity}, transport {Transport}, sync bytes {Sync}, PCR gaps {Pcr}, datagrams lost {Datagrams}, packets without key {NoKey}, held back {Held}, outages {Outages}; frozen {Frozen}, lock {Locked}, descrambling {Descrambling}, level {Level}, quality {Quality}.")]
    private partial void LogDisturbance(string channel, long continuity, long transport, long sync, long pcr, long datagrams, long noKey, long held, long outages, bool frozen, bool locked, string descrambling, string level, string quality);

    [LoggerMessage(EventId = 123, Level = LogLevel.Information, Message = "'{Channel}' ran {Seconds} s: continuity errors {Continuity}, transport errors {Transport}, sync bytes {Sync}, datagrams lost {Datagrams}, gaps repaired {Gaps}, outages bridged {Outages}, frozen {FrozenMs} ms.")]
    private partial void LogSummary(string channel, long seconds, long continuity, long transport, long sync, long datagrams, long gaps, long outages, long frozenMs);

    [LoggerMessage(EventId = 120, Level = LogLevel.Information, Message = "Streaming '{Channel}' to {Client} (repair: {Repair}).")]
    private partial void LogOpened(string channel, string client, bool repair);

    [LoggerMessage(EventId = 121, Level = LogLevel.Information, Message = "Stopped '{Channel}' for {Client} after {Bytes} bytes.")]
    private partial void LogClosed(string channel, string client, long bytes);
}
