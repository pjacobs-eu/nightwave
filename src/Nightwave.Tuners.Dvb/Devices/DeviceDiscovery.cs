using System.Net;
using Nightwave.Core.Tuning;
using Microsoft.Extensions.Logging;
using Nightwave.Tuners.Dvb.LinuxDvb;

namespace Nightwave.Tuners.Dvb.Devices;

/// <summary>A SAT>IP server that answered, with what its description says.</summary>
internal sealed record DiscoveredSatIpServer(string Host, SatIpDeviceDescription Description);

/// <summary>Asks the network which devices there are. An interface, so tests need no network.</summary>
internal interface IDeviceDiscovery
{
    Task<IReadOnlyList<DiscoveredSatIpServer>> FindSatIpServersAsync(CancellationToken cancellationToken);

    /// <summary>Asks one address, for a device the operator wrote down.</summary>
    Task<DiscoveredSatIpServer?> ProbeSatIpServerAsync(string host, CancellationToken cancellationToken);

    /// <summary>
    /// Reads a SAT>IP device description from where the operator said it is, without searching.
    /// The server is the host in that URL.
    /// </summary>
    Task<DiscoveredSatIpServer?> DescribeSatIpServerAsync(Uri descriptionUrl, CancellationToken cancellationToken);
}

/// <summary>Discovery over SSDP and HTTP.</summary>
internal sealed partial class NetworkDeviceDiscovery : IDeviceDiscovery
{
    private static readonly TimeSpan SearchWait = TimeSpan.FromSeconds(2);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<NetworkDeviceDiscovery> _logger;

    public NetworkDeviceDiscovery(IHttpClientFactory httpClientFactory, ILogger<NetworkDeviceDiscovery> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<DiscoveredSatIpServer>> FindSatIpServersAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SsdpAnswer> answers;

        try
        {
            answers = await Ssdp.SearchAsync(Ssdp.SatIpSearchTarget, SearchWait, null, cancellationToken).ConfigureAwait(false);
        }
        catch (System.Net.Sockets.SocketException error)
        {
            // No multicast here - a container without host networking, typically. Devices
            // written down in the settings still work.
            LogSearchFailed("SSDP", error.Message);
            return [];
        }

        var servers = new List<DiscoveredSatIpServer>();

        foreach (var answer in answers)
        {
            if (await DescribeAsync(answer, cancellationToken).ConfigureAwait(false) is { } server)
            {
                servers.Add(server);
            }
        }

        return servers;
    }

    public async Task<DiscoveredSatIpServer?> ProbeSatIpServerAsync(string host, CancellationToken cancellationToken)
    {
        IPAddress? address;

        try
        {
            address = IPAddress.TryParse(host, out var parsed)
                ? parsed
                : (await Dns.GetHostAddressesAsync(host, System.Net.Sockets.AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false))
                    .FirstOrDefault();
        }
        catch (System.Net.Sockets.SocketException error)
        {
            LogProbeFailed(host, error.Message);
            return null;
        }

        if (address is null)
        {
            return null;
        }

        try
        {
            var answers = await Ssdp.SearchAsync(Ssdp.SatIpSearchTarget, SearchWait, new IPEndPoint(address, 1900), cancellationToken)
                .ConfigureAwait(false);

            foreach (var answer in answers)
            {
                if (await DescribeAsync(answer, cancellationToken).ConfigureAwait(false) is { } server)
                {
                    return server with { Host = host };
                }
            }
        }
        catch (System.Net.Sockets.SocketException error)
        {
            LogProbeFailed(host, error.Message);
        }

        // A FRITZ!Box often answers no search, but has its description at a fixed place.
        return await DescribeSatIpServerAsync(new Uri($"http://{address}:49000/satipdesc.xml"), cancellationToken).ConfigureAwait(false) is { } fritz
            ? fritz with { Host = host }
            : null;
    }

    public async Task<DiscoveredSatIpServer?> DescribeSatIpServerAsync(Uri descriptionUrl, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(DvbProvider.DeviceClientName);
            var xml = await client.GetStringAsync(descriptionUrl, cancellationToken).ConfigureAwait(false);

            if (SatIpDeviceDescription.Parse(xml) is { } description)
            {
                return new DiscoveredSatIpServer(descriptionUrl.Host, description);
            }

            LogDescriptionUnreadable(descriptionUrl);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            LogProbeFailed(descriptionUrl, error.Message);
        }

        return null;
    }

    private async Task<DiscoveredSatIpServer?> DescribeAsync(SsdpAnswer answer, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(DvbProvider.DeviceClientName);
            var xml = await client.GetStringAsync(answer.Location, cancellationToken).ConfigureAwait(false);

            if (SatIpDeviceDescription.Parse(xml) is { } description)
            {
                return new DiscoveredSatIpServer(answer.Address.ToString(), description);
            }

            LogDescriptionUnreadable(answer.Location);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            LogProbeFailed(answer.Location, error.Message);
        }

        return null;
    }

    [LoggerMessage(EventId = 9800, Level = LogLevel.Warning, Message = "The {Protocol} device search could not be sent: {Reason}. Devices listed in the tuner settings are still used.")]
    private partial void LogSearchFailed(string protocol, string reason);

    [LoggerMessage(EventId = 9801, Level = LogLevel.Information, Message = "No device answered at '{Address}': {Reason}")]
    private partial void LogProbeFailed(string address, string reason);

    [LoggerMessage(EventId = 9803, Level = LogLevel.Information, Message = "No device description could be read from '{Location}': {Reason}")]
    private partial void LogProbeFailed(Uri location, string reason);

    [LoggerMessage(EventId = 9802, Level = LogLevel.Warning, Message = "The SAT>IP device description at '{Location}' could not be read.")]
    private partial void LogDescriptionUnreadable(Uri location);
}

/// <summary>
/// The devices the plugin can use: those in the settings, always, and those found on the
/// network when discovery is on. A search takes a couple of seconds, so its outcome is kept for
/// a while - tuning a channel should not wait for the network to be asked again. What was
/// detected on their inputs is added each time they are asked for, so a detection shows at once.
/// </summary>
internal sealed class DeviceDirectory : IDisposable
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly DvbSettingsProvider _settings;
    private readonly IDeviceDiscovery _discovery;
    private readonly TimeProvider _time;
    private readonly InputCache _inputs;
    private readonly bool _ownsInputs;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    private IReadOnlyList<TunerDevice>? _devices;
    private DateTimeOffset _foundAt = DateTimeOffset.MinValue;

    public DeviceDirectory(DvbSettingsProvider settings, IDeviceDiscovery discovery, TimeProvider time, InputCache? inputs = null)
    {
        _settings = settings;
        _discovery = discovery;
        _time = time;
        _ownsInputs = inputs is null;
        _inputs = inputs ?? new InputCache(null);
        _settings.Changed += (_, _) => _devices = null;
    }

    /// <summary>What was detected on the devices' inputs.</summary>
    public InputCache Inputs => _inputs;

    public void Dispose()
    {
        _mutex.Dispose();

        if (_ownsInputs)
        {
            _inputs.Dispose();
        }
    }

    /// <summary>Asks a card what it receives. Replaced in tests.</summary>
    internal Func<DvbCardSettings, DvbCard?> DescribeCard { get; init; } = ReadCard;

    /// <summary>The devices, found again when <paramref name="refresh"/> asks or the last search is old.</summary>
    public async Task<IReadOnlyList<TunerDevice>> GetDevicesAsync(bool refresh, CancellationToken cancellationToken)
    {
        var devices = await FindCachedAsync(refresh, cancellationToken).ConfigureAwait(false);
        var inputs = await _inputs.GetAllAsync(cancellationToken).ConfigureAwait(false);

        return [.. devices.Select(device => WithInputs(device, inputs.GetValueOrDefault(device.Id)))];
    }

    /// <summary>A device with what was detected on its inputs, or why nothing is known yet.</summary>
    internal static TunerDevice WithInputs(TunerDevice device, DeviceInputs? inputs)
    {
        var satellites = inputs?.SatelliteInputs() ?? [];

        return device with
        {
            SatelliteInputs = satellites,
            InputsProblem = satellites.Count > 0
                ? null
                : inputs is null
                    ? $"The inputs of {device.Label} have not been checked for a satellite yet."
                    : $"No satellite was found on any input of {device.Label}: {inputs.DescribeFailures()}.",
        };
    }

    private async Task<IReadOnlyList<TunerDevice>> FindCachedAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (!refresh && _devices is { } cached && _time.GetUtcNow() - _foundAt < CacheDuration)
        {
            return cached;
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!refresh && _devices is { } again && _time.GetUtcNow() - _foundAt < CacheDuration)
            {
                return again;
            }

            var devices = await FindAsync(cancellationToken).ConfigureAwait(false);
            _devices = devices;
            _foundAt = _time.GetUtcNow();

            return devices;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<IReadOnlyList<TunerDevice>> FindAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        // What an earlier version had typed in per input, kept as a guess until it is checked.

        var discovered = settings.AutoDiscover
            ? await _discovery.FindSatIpServersAsync(cancellationToken).ConfigureAwait(false)
            : [];

        var devices = new List<TunerDevice>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in settings.Devices.Where(entry => !string.IsNullOrWhiteSpace(entry.EffectiveAddress())))
        {
            var (host, port) = SplitAddress(entry.EffectiveAddress());
            claimed.Add(host);

            // A description URL is the operator saying where to look, so it is read first and on
            // its own: a server that needs one is a server that does not answer the search anyway.
            var found = entry.TryDescriptionUri() is { } descriptionUrl
                ? await _discovery.DescribeSatIpServerAsync(descriptionUrl, cancellationToken).ConfigureAwait(false)
                : discovered.FirstOrDefault(device => string.Equals(device.Host, host, StringComparison.OrdinalIgnoreCase))
                    ?? await _discovery.ProbeSatIpServerAsync(host, cancellationToken).ConfigureAwait(false);

            devices.Add(DeviceMapping.SatIp(host, port, found, entry));
        }

        devices.AddRange(discovered
            .Where(device => !claimed.Contains(device.Host))
            .Select(device => DeviceMapping.SatIp(device.Host, null, device, null)));

        devices.AddRange(settings.DvbCards.Select(entry => DeviceMapping.Card(entry, DescribeCard(entry))));

        return [.. devices.DistinctBy(device => device.Id, StringComparer.OrdinalIgnoreCase)];
    }

    private static DvbCard? ReadCard(DvbCardSettings entry)
    {
        var card = DeviceMapping.CardOf(entry);

        return LinuxDvbKernel.Unsupported is null
            ? DvbCards.Describe(LinuxDvbKernel.Instance, card.FrontendPath, card.Adapter, card.Frontend)
            : null;
    }

    /// <summary>Host and port of an address typed as <c>host</c>, <c>host:port</c> or <c>[v6]:port</c>.</summary>
    public static (string Host, int? Port) SplitAddress(string address)
    {
        var trimmed = address.Trim();

        if (trimmed.StartsWith('['))
        {
            var close = trimmed.IndexOf(']', StringComparison.Ordinal);
            if (close > 0)
            {
                var host = trimmed[1..close];
                var rest = trimmed[(close + 1)..];

                return rest.StartsWith(':') && int.TryParse(rest[1..], System.Globalization.CultureInfo.InvariantCulture, out var v6Port)
                    ? (host, v6Port)
                    : (host, null);
            }
        }

        var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && colon == trimmed.LastIndexOf(':')
            && int.TryParse(trimmed[(colon + 1)..], System.Globalization.CultureInfo.InvariantCulture, out var port))
        {
            return (trimmed[..colon], port);
        }

        return (trimmed, null);
    }
}

/// <summary>Turns what was found and what was configured into one <see cref="TunerDevice"/>.</summary>
internal static class DeviceMapping
{
    /// <summary>The id of the SAT>IP server at <paramref name="host"/>: stable, and what channels and scan targets refer to.</summary>
    public static string IdFor(string host) => $"satip:{host.Trim().ToLowerInvariant()}";

    public static LocalDvbCard CardOf(DvbCardSettings entry) => new(entry.Adapter, entry.Frontend, entry.Dish);

    /// <summary>A frontend of a card: one tuner, whatever it receives.</summary>
    public static TunerDevice Card(DvbCardSettings entry, DvbCard? found)
    {
        var card = CardOf(entry);
        var host = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"adapter{entry.Adapter}.{entry.Frontend}");

        return new TunerDevice
        {
            Id = $"dvb:{host}",
            Name = !string.IsNullOrWhiteSpace(entry.Name) ? entry.Name.Trim() : found?.Name ?? host,
            Host = host,
            Card = card,
            ReportedName = found?.Name,
            Tuners = found is null ? new Dictionary<DeliverySystem, int>() : DvbCards.Tuners(found),
            TunersKnown = found is not null,
            MaxTuners = 1,
            Error = found is null
                ? LinuxDvbKernel.Unsupported ?? $"{card.FrontendPath} could not be read. Check that the card's driver is loaded, that a container was given the device, and that Nightwave's user is in its group, usually 'video'."
                : null,
        };
    }

    public static TunerDevice SatIp(string host, int? rtspPort, DiscoveredSatIpServer? found, SatIpDeviceSettings? entry)
    {
        var name = !string.IsNullOrWhiteSpace(entry?.Name)
            ? entry.Name.Trim()
            : found?.Description.FriendlyName ?? host;

        return new TunerDevice
        {
            Id = IdFor(host),
            Name = name,
            Host = host,
            RtspPort = rtspPort ?? 554,
            ReportedName = found?.Description.FriendlyName,
            ModelName = found?.Description.ModelName,
            Manufacturer = found?.Description.Manufacturer,
            Capabilities = found?.Description.Capabilities,
            Tuners = found?.Description.Tuners ?? new Dictionary<DeliverySystem, int>(),
            TunersKnown = found is not null && found.Description.Tuners.Count > 0,
            MaxTuners = Math.Max(0, entry?.MaxTuners ?? 0),
            Error = found is null && entry is not null
                ? entry.TryDescriptionUri() is { } descriptionUrl
                    ? $"The SAT>IP device description at {descriptionUrl} could not be read, so the tuners of {host} are not known. It is used with {Math.Max(entry.MaxTuners, 1)} tuner(s); check the URL in a browser, or set 'Tuners to use' to the number it has."
                    : $"{host} did not answer a SAT>IP search, so its tuners are not known. It is used with {Math.Max(entry.MaxTuners, 1)} tuner(s); enter its description URL, or set 'Tuners to use' to the number it has."
                : null,
        };
    }
}
