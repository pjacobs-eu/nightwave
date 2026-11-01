using Nightwave.Core.Tuning;
using Microsoft.Extensions.Logging;

namespace Nightwave.Tuners.Dvb.Descrambling;

/// <summary>
/// Opens scrambled services through the descrambling server's network dvbapi. One
/// <see cref="DvbApiClient"/> per server (host, port and client name); all services on it share
/// its connection.
/// </summary>
/// <remarks>
/// <see cref="IsConfigured"/> has to answer synchronously, and the options come from an async
/// source. So the options are cached: loaded once when the factory is created, reloaded in the
/// background when <see cref="IsConfigured"/> finds them older than
/// <see cref="DvbApiTimings.OptionsRefreshInterval"/>, and reloaded for certain by every
/// <see cref="OpenAsync"/>. Only the very first answer waits, briefly
/// (<see cref="DvbApiTimings.InitialOptionsWait"/>), for the first load to finish; after that a
/// changed setting shows within the refresh interval, and at the latest with the next channel
/// that is opened.
/// </remarks>
internal sealed partial class DvbApiDescramblerFactory : IServiceDescramblerFactory, IAsyncDisposable
{
    private readonly IDvbApiOptionsSource _optionsSource;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly DvbApiTimings _timings;
    private readonly TimeProvider _time;
    private readonly Dictionary<(string Host, int Port, string ClientName), DvbApiClient> _clients = [];
    private readonly Lock _clientsLock = new();
    private readonly Task _initialLoad;
    private DvbApiOptions? _options;
    private long _loadedAt;
    private int _refreshing;

    public DvbApiDescramblerFactory(IDvbApiOptionsSource options, ILoggerFactory loggerFactory)
        : this(options, loggerFactory, DvbApiTimings.Default, TimeProvider.System)
    {
    }

    internal DvbApiDescramblerFactory(
        IDvbApiOptionsSource options,
        ILoggerFactory loggerFactory,
        DvbApiTimings timings,
        TimeProvider time)
    {
        _optionsSource = options;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<DvbApiDescramblerFactory>();
        _timings = timings;
        _time = time;
        _initialLoad = Task.Run(() => RefreshQuietlyAsync());
    }

    public bool IsConfigured
    {
        get
        {
            if (!_initialLoad.IsCompleted)
            {
                // Bounded, and the load does not need this thread: it runs on the pool.
                _initialLoad.Wait(_timings.InitialOptionsWait);
            }
            else if (_time.GetElapsedTime(Interlocked.Read(ref _loadedAt)) > _timings.OptionsRefreshInterval
                && Interlocked.CompareExchange(ref _refreshing, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await RefreshQuietlyAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        Volatile.Write(ref _refreshing, 0);
                    }
                });
            }

            return Volatile.Read(ref _options) is not null;
        }
    }

    public async Task<IServiceDescrambler> OpenAsync(ServiceDescramblingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var options = await RefreshAsync(cancellationToken).ConfigureAwait(false)
            ?? throw DescramblerErrors.NotConfigured(request.ChannelName);

        if (request.Slot is < 0 or >= CaProgramMap.MaxSlots)
        {
            throw DescramblerErrors.TooManyServices(request.ChannelName, request.Slot);
        }

        CaProgramMap programMap;
        try
        {
            programMap = CaProgramMap.Parse(request.ProgramMapSection.Span);
        }
        catch (FormatException exception)
        {
            throw DescramblerErrors.InvalidProgramMap(request.ChannelName, exception.Message);
        }

        if (programMap.ProgramNumber != request.ServiceId)
        {
            throw DescramblerErrors.InvalidProgramMap(
                request.ChannelName,
                $"it is the program map of program {programMap.ProgramNumber}, not of service {request.ServiceId}");
        }

        var client = ClientFor(options);
        var service = new DvbApiServiceDescrambler(
            request,
            programMap,
            client,
            _loggerFactory.CreateLogger<DvbApiServiceDescrambler>(),
            _time);

        await client.AddServiceAsync(service, cancellationToken).ConfigureAwait(false);
        service.StartWatching();
        var device = CaProgramMap.DeviceNumber(request.Slot);
        LogOpened(request.ChannelName, request.ServiceId, request.Slot, device, client.Endpoint, programMap.EcmPids.Count);
        return service;
    }

    public async ValueTask DisposeAsync()
    {
        DvbApiClient[] clients;
        lock (_clientsLock)
        {
            clients = [.. _clients.Values];
            _clients.Clear();
        }

        foreach (var client in clients)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private DvbApiClient ClientFor(DvbApiOptions options)
    {
        // A client without services holds no connection, so the ones for options that have
        // since changed cost nothing and are kept until the factory goes.
        lock (_clientsLock)
        {
            var key = (options.Host, options.Port, options.ClientName);
            if (!_clients.TryGetValue(key, out var client))
            {
                client = new DvbApiClient(options, _timings, _loggerFactory.CreateLogger<DvbApiClient>(), _time);
                _clients[key] = client;
            }

            return client;
        }
    }

    private async Task<DvbApiOptions?> RefreshAsync(CancellationToken cancellationToken)
    {
        var options = await _optionsSource.GetAsync(cancellationToken).ConfigureAwait(false);
        if (options is not null && (string.IsNullOrWhiteSpace(options.Host) || options.Port is <= 0 or > 65535))
        {
            options = null;
        }

        Volatile.Write(ref _options, options);
        Interlocked.Exchange(ref _loadedAt, _time.GetTimestamp());
        return options;
    }

    private async Task RefreshQuietlyAsync()
    {
        try
        {
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A failed background refresh keeps the last options; the next OpenAsync reports the error.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            LogOptionsFailed(exception);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Descrambling '{Channel}' (service {ServiceId}, slot {Slot}) through the descrambling server at {Endpoint} as adapter, demux and ca device {Device}, {EcmPids} ECM pids")]
    private partial void LogOpened(string channel, int serviceId, int slot, int device, string endpoint, int ecmPids);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the dvbapi settings failed; keeping the last known ones")]
    private partial void LogOptionsFailed(Exception exception);
}
