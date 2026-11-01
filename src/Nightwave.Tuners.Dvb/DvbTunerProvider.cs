using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb.Channels;
using Nightwave.Tuners.Dvb.Descrambling;
using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.Epg;
using Nightwave.Tuners.Dvb.Streaming;
using Nightwave.Tuners.Dvb.Transport;
using Microsoft.Extensions.Logging;

namespace Nightwave.Tuners.Dvb;

/// <summary>
/// The channels scans found, tuned on whichever device can receive them. A satellite channel
/// belongs to its orbital position rather than to a device, so two SAT>IP servers on the same
/// dish are one pool of tuners.
/// </summary>
/// <remarks>
/// A channel is handed over as bytes rather than an address: the tuner is shared between
/// channels of one transponder, and what a player gets is one service cut out of it, with the
/// descrambling already done.
/// <para>
/// The logos of satellite channels come from the picon archive shipped beside the program.
/// </para>
/// </remarks>
internal sealed partial class DvbTunerProvider : ITunerProvider, IChannelLogoSource
{
    private readonly ChannelStore _store;
    private readonly DeviceDirectory _devices;
    private readonly TunerPool _pool;
    private readonly IServiceDescramblerFactory _descramblers;
    private readonly PiconCatalogue _picons;
    private readonly ILogger<DvbTunerProvider> _logger;

    public DvbTunerProvider(
        ChannelStore store,
        DeviceDirectory devices,
        TunerPool pool,
        IServiceDescramblerFactory descramblers,
        ILogger<DvbTunerProvider> logger,
        PiconCatalogue? picons = null)
    {
        _store = store;
        _devices = devices;
        _pool = pool;
        _descramblers = descramblers;
        _picons = picons ?? new PiconCatalogue(null);
        _logger = logger;

        if (_picons.Count == 0)
        {
            LogNoPicons();
        }
    }

    public string ProviderId => DvbProvider.Id;

    public string DisplayNameKey => "provider.satip.name";

    public TunerCapabilities Capabilities => TunerCapabilities.StreamSharing | TunerCapabilities.ChannelScan;

    public async Task<IReadOnlyList<TvChannel>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        var channels = await _store.GetChannelsAsync(cancellationToken).ConfigureAwait(false);

        return
        [
            .. channels.Select(channel => new TvChannel
            {
                Id = channel.Id,
                ProviderId = ProviderId,
                Name = channel.Name,
                Number = channel.Number,
                Kind = channel.Kind,

                // The DVB service, which is what the broadcast guide keys its programmes by.
                GuideId = new DvbServiceKey(channel.OriginalNetworkId, channel.TransportStreamId, channel.ServiceId).GuideId,

                // Broadcast scrambled, whether or not a descrambling server can open it: the lock
                // in the lineup says pay TV, as the scan's count of encrypted channels does. Whether
                // it can be opened is answered when it is tuned (see OpenAsync).
                IsEncrypted = channel.Scrambled,
                Broadcast = Facts(channel),
            }),
        ];
    }

    /// <summary>What the scan recorded, in the terms a list of channels shows.</summary>
    private static BroadcastFacts Facts(StoredChannel channel)
    {
        var tuning = channel.Tuning;

        return new BroadcastFacts
        {
            Source = channel.OrbitalPosition is { } tenths ? new OrbitalPosition(tenths).ToString() : channel.DeviceId ?? "–",
            ServiceProvider = channel.Provider,
            ServiceType = channel.ServiceType,
            NetworkId = channel.OriginalNetworkId,
            TransportStreamId = channel.TransportStreamId,
            ServiceId = channel.ServiceId,
            PmtPid = channel.PmtPid,
            PcrPid = channel.PcrPid,
            Pids = channel.Pids,
            CaSystems = channel.CaSystems,
            EcmPids = channel.EcmPids,
            System = tuning?.System switch
            {
                Tuning.DvbSystem.DvbS => "DVB-S",
                Tuning.DvbSystem.DvbS2 => "DVB-S2",
                Tuning.DvbSystem.DvbC => "DVB-C",
                Tuning.DvbSystem.DvbT => "DVB-T",
                Tuning.DvbSystem.DvbT2 => "DVB-T2",
                _ => null,
            },
            FrequencyMhz = tuning is null ? null : tuning.FrequencyKhz / 1000d,
            Polarization = tuning?.Polarization switch
            {
                Tuning.Polarization.Horizontal => "H",
                Tuning.Polarization.Vertical => "V",
                Tuning.Polarization.CircularLeft => "L",
                Tuning.Polarization.CircularRight => "R",
                _ => null,
            },
            SymbolRate = tuning?.SymbolRate,
            Modulation = tuning?.Modulation,
            Fec = tuning?.Fec,
            RollOff = tuning?.RollOff,
            Pilots = tuning?.Pilots,
        };
    }

    public bool HasLogo(string channelId) => _picons.Has(channelId);

    public ChannelLogo? FindLogo(string channelId, LogoBackground background) =>
        _picons.Find(channelId, background);

    public async Task<IReadOnlyList<TunerStatus>> GetTunersAsync(CancellationToken cancellationToken = default)
    {
        var devices = await _devices.GetDevicesAsync(refresh: false, cancellationToken).ConfigureAwait(false);

        return
        [
            .. devices.Select(device =>
            {
                var usage = _pool.Usage(device);
                var problems = _pool.Problems(device);

                return new TunerStatus
                {
                    TunerId = device.Id,
                    Name = device.Label,
                    MaxStreams = device.TotalCapacity,
                    ActiveStreams = usage.Sum(use => use.Tuners),
                    ActiveChannelId = usage.SelectMany(use => use.Channels).FirstOrDefault(),
                    // A device that works but a channel on it that cannot be descrambled is still
                    // something the operator has to see.
                    Error = device.Error ?? (problems.Count == 0 ? null : string.Join(" ", problems)),
                };
            }),
        ];
    }

    public async Task<ITunedStream> OpenAsync(TunedStreamRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var channel = await _store.FindAsync(request.ChannelId, cancellationToken).ConfigureAwait(false)
            ?? throw DvbErrors.ChannelMissing(request.ChannelId);

        // Refused before a tuner is taken: a scrambled channel that nothing can open would only
        // hold a tuner somebody else could use while it fails.
        if (channel.Scrambled && !_descramblers.IsConfigured)
        {
            throw DvbErrors.DescramblingNotConfigured(channel.Name);
        }

        var devices = await _devices.GetDevicesAsync(refresh: false, cancellationToken).ConfigureAwait(false);
        var candidates = Candidates(channel, devices);

        if (candidates.Count == 0)
        {
            throw DvbErrors.NoDevice(channel.Name, Source(channel));
        }

        var lease = await _pool.OpenAsync(channel, candidates, request.ReplacesOwnStream ? request.ClientDeviceId : null, cancellationToken).ConfigureAwait(false);

        LogChannelOpened(channel.Name, lease.Multiplex.Device.Label, request.ClientDeviceId);

        return new DvbTunedStream(lease);
    }

    /// <summary>
    /// The devices and inputs that can receive a channel, the ones not reporting an error first.
    /// Any SAT>IP input the channel's satellite was detected on will do; cable and terrestrial
    /// channels stay on the device that found them.
    /// </summary>
    internal static IReadOnlyList<TuneCandidate> Candidates(StoredChannel channel, IEnumerable<TunerDevice> devices)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(devices);

        var candidates = new List<TuneCandidate>();

        foreach (var device in devices.OrderBy(device => device.Error is null ? 0 : 1))
        {
            switch (channel.Delivery)
            {
                case DeliverySystem.Satellite when channel.Tuning is { } tuning:
                    // A tenth of a degree either way is the same satellite, written the way
                    // another network or an earlier version rounded it.
                    candidates.AddRange(device.SatelliteInputs
                        .Where(input => channel.OrbitalPosition is { } position && Math.Abs(input.Position.Tenths - position) <= 1)
                        .Where(_ => device.Capacity(DeliverySystem.Satellite) > 0)
                        .Select(input => new TuneCandidate(device, new TuneRequest(tuning, input.Source))));
                    break;

                case DeliverySystem.Cable or DeliverySystem.Terrestrial
                    when IsSameDevice(channel, device) && channel.Tuning is { } tuning:
                    candidates.Add(new TuneCandidate(device, new TuneRequest(tuning, null)));
                    break;
            }
        }

        return candidates;
    }

    private static bool IsSameDevice(StoredChannel channel, TunerDevice device) =>
        string.Equals(channel.DeviceId, device.Id, StringComparison.OrdinalIgnoreCase);

    private static string Source(StoredChannel channel) => channel.Delivery switch
    {
        DeliverySystem.Satellite when channel.OrbitalPosition is { } tenths => new OrbitalPosition(tenths).ToString(),
        _ => channel.DeviceId ?? "its device",
    };

    [LoggerMessage(EventId = 9871, Level = LogLevel.Information, Message = "No picons.zip was found beside the program or in the data directory, so satellite channels have no logos. Build it with packaging/picons.sh.")]
    private partial void LogNoPicons();

    [LoggerMessage(EventId = 9870, Level = LogLevel.Information, Message = "Opened channel '{Channel}' on {Device} for device '{ClientDevice}'.")]
    private partial void LogChannelOpened(string channel, string device, string clientDevice);
}

/// <summary>
/// A tuned channel. Every read of it is a new reader of the same service; disposing it gives
/// the viewer back, and with the last viewer on the transponder, the tuner. It also says where
/// it comes from and how it is doing, for the player's details panel.
/// </summary>
internal sealed class DvbTunedStream : ITunedStream, ITunedStreamDetails, ITunedStreamTelemetry
{
    /// <summary>How far back "lost recently" reaches.</summary>
    private static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(1);

    private readonly ServiceLease _lease;
    private readonly Queue<(long At, long Lost)> _lossSamples = new();
    private readonly Lock _samples = new();

    public DvbTunedStream(ServiceLease lease)
    {
        _lease = lease;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string ChannelId => _lease.Feed.Channel.Id;

    public bool IsActive => !_lease.IsReleased && !_lease.Feed.IsFaulted;

    public SignalReading? Signal => _lease.Multiplex.TransportStatus.Signal is { } signal
        ? new SignalReading
        {
            Locked = signal.Locked,
            Level = signal.Measured ? signal.Level / 255d : null,
            Quality = signal.Measured ? signal.Quality / 15d : null,
            LevelRaw = signal.LevelRaw ?? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{signal.Level} / 255"),
            QualityRaw = signal.QualityRaw ?? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{signal.Quality} / 15"),
            SnrDb = signal.SnrDb,
            BitErrorRate = signal.BitErrorRate,
        }
        : null;

    public TransportReading? Transport
    {
        get
        {
            var status = _lease.Multiplex.TransportStatus;

            return new TransportReading
            {
                Path = status.Path switch
                {
                    StreamPath.RtspInterleaved => "RTP in RTSP (TCP)",
                    StreamPath.RtpUdp => "RTP (UDP)",
                    StreamPath.LocalCard => "DVB card",
                    _ => null,
                },
                DatagramsReceived = status.DatagramsReceived,
                DatagramsLost = status.DatagramsLost,
                DatagramsReordered = status.DatagramsReordered,
            };
        }
    }

    public string? DeviceName => _lease.Multiplex.Device.ReportedName ?? _lease.Multiplex.Device.Name;

    public int? Input => _lease.Multiplex.Request.Source;

    public int? FrontEnd => _lease.Multiplex.TransportStatus.Signal?.FrontEnd;

    public bool? Pilots => _lease.Multiplex.TransportStatus.Signal?.Pilots;

    public DescramblingReading? Descrambling
    {
        get
        {
            var feed = _lease.Feed;

            if (!feed.IsScrambled)
            {
                return null;
            }

            var status = feed.DescramblingStatus;
            var problem = status?.Problem ?? feed.DescramblingProblem;
            var ecm = status?.LastEcm;
            var times = status?.EcmTimes ?? new EcmTimes();

            return new DescramblingReading
            {
                State = !feed.CanDescramble ? DescramblingState.NotConfigured
                    : problem is not null ? DescramblingState.Problem
                    : status is { HasKeys: true } ? DescramblingState.Active
                    : DescramblingState.WaitingForKeys,
                Problem = problem?.Message,
                Server = status?.Server,
                ServerVersion = status?.ServerVersion,
                Connected = status?.Connected ?? false,
                CaId = ecm?.CaId,
                CardSystem = ecm?.CardSystem,
                EcmPid = ecm?.Pid,
                ProviderId = ecm?.ProviderId,
                Reader = ecm?.Reader,
                Source = ecm?.Source,
                Protocol = ecm?.Protocol,
                Hops = ecm?.Hops,
                EcmTimeMs = ecm?.EcmTimeMilliseconds,
                EcmTimeFastestMs = times.Count > 0 ? times.Fastest : null,
                EcmTimeAverageMs = times.Count > 0 ? (double)times.Total / times.Count : null,
                EcmTimeSlowestMs = times.Count > 0 ? times.Slowest : null,
                EcmsAnswered = times.Count,
                EcmsForwarded = status?.EcmSectionsForwarded ?? 0,
                OtherSectionsForwarded = status?.OtherSectionsForwarded ?? 0,
                EmmsForwarded = status?.EmmSectionsForwarded ?? 0,
                SectionsDropped = status?.SectionsDropped ?? 0,
                KeysReceived = status?.KeysReceived ?? 0,
                Algorithm = status?.Algorithm,
                StillScrambledPackets = feed.StillScrambledPackets,
            };
        }
    }

    public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_lease.IsReleased, this);

        return Task.FromResult<Stream>(_lease.Feed.Subscribe());
    }

    public ValueTask DisposeAsync() => _lease.DisposeAsync();

    public IReadOnlyList<TunedStreamDetailGroup> GetDetails()
    {
        var multiplex = _lease.Multiplex;
        var feed = _lease.Feed;
        var transport = multiplex.TransportStatus;

        return StreamDetails.Build(new StreamSnapshot
        {
            Device = multiplex.Device,
            Request = multiplex.Request,
            Channel = feed.Channel,
            SharedWith = [.. multiplex.Channels.Where(channel => channel.Id != feed.Channel.Id).Select(channel => channel.Name)],
            TunersInUse = _lease.Pool.Usage(multiplex.Device).Sum(use => use.Tuners),
            Transport = transport,
            RecentlyLost = RecentlyLost(transport.DatagramsLost),
            Pids = feed.OutputPids,
            Scrambled = feed.IsScrambled,
            CanDescramble = feed.CanDescramble,
            Descrambling = feed.DescramblingStatus,
            DescramblingProblem = feed.DescramblingProblem,
            StillScrambledPackets = feed.StillScrambledPackets,
        });
    }

    /// <summary>
    /// Datagrams lost within the last minute, from the totals seen at each call - the panel asks
    /// every couple of seconds. Null until a minute of history is there.
    /// </summary>
    private long? RecentlyLost(long lost)
    {
        var now = Environment.TickCount64;

        lock (_samples)
        {
            _lossSamples.Enqueue((now, lost));

            while (_lossSamples.Count > 1 && now - _lossSamples.ElementAt(1).At >= RecentWindow.TotalMilliseconds)
            {
                _lossSamples.Dequeue();
            }

            var (oldestAt, oldestLost) = _lossSamples.Peek();

            return now - oldestAt >= RecentWindow.TotalMilliseconds ? lost - oldestLost : null;
        }
    }
}
