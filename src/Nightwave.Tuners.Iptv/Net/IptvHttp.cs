using System.Net;
using Nightwave.Core.Configuration;
using Nightwave.Core.Tuning;

namespace Nightwave.Tuners.Iptv.Net;

/// <summary>
/// Hands out the HTTP clients of web tuners: one for lists, cut off after a while, and one for
/// streams, never cut off. Each tuner has its own pair, because each has its own proxy or none.
/// </summary>
/// <remarks>
/// It fails closed. A tuner with a proxy switched on and an address that cannot be used gets no
/// client at all: a request that was meant to go through a proxy and went out directly instead
/// is exactly what somebody configured a proxy to prevent. A client with no proxy never uses the
/// machine's own proxy settings either.
/// </remarks>
public sealed class IptvHttp : IDisposable
{
    public const string ProviderId = "iptv";

    /// <summary>What <see cref="SocketsHttpHandler"/> knows how to speak to.</summary>
    public static readonly IReadOnlySet<string> SupportedSchemes =
        new HashSet<string>(["http", "https", "socks4", "socks4a", "socks5"], StringComparer.OrdinalIgnoreCase);

    private readonly ConfigStore _config;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (string Route, HttpClient Client)> _clients = new(StringComparer.Ordinal);

    public IptvHttp(ConfigStore config)
    {
        ArgumentNullException.ThrowIfNull(config);

        _config = config;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var (_, client) in _clients.Values)
            {
                client.Dispose();
            }

            _clients.Clear();
        }
    }

    public HttpClient Api(TunerConfig tuner) => Client(tuner, stream: false);

    public HttpClient Stream(TunerConfig tuner) => Client(tuner, stream: true);

    public HttpClient Api(string tunerId) => Client(Tuner(tunerId), stream: false);

    public HttpClient Stream(string tunerId) => Client(Tuner(tunerId), stream: true);

    /// <summary>The proxy's address without credentials, or null where the tuner goes out directly.</summary>
    /// <exception cref="TunerException">A proxy is switched on and its address cannot be used.</exception>
    public static Uri? AddressOf(TunerConfig tuner)
    {
        ArgumentNullException.ThrowIfNull(tuner);

        if (tuner.EffectiveProxy is not { } proxy)
        {
            return null;
        }

        if (!Uri.TryCreate(proxy.Url.Trim(), UriKind.Absolute, out var address)
            || !SupportedSchemes.Contains(address.Scheme)
            || string.IsNullOrEmpty(address.Host))
        {
            throw new TunerException(
                ProviderId,
                "proxy.invalid",
                $"The proxy of tuner '{tuner.Name}' is not an address it can use, so nothing was sent.",
                "Write it as http://host:port, https://host:port, socks4://host:port, socks4a://host:port or socks5://host:port, or switch the proxy off for this tuner.")
            {
                Values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["tuner"] = tuner.Name },
            };
        }

        return new UriBuilder(address.Scheme, address.Host, address.Port).Uri;
    }

    /// <summary>The credentials from their own fields, or from the address where they were written into it.</summary>
    public static NetworkCredential? CredentialsOf(TunerConfig tuner)
    {
        ArgumentNullException.ThrowIfNull(tuner);

        if (tuner.EffectiveProxy is not { } proxy)
        {
            return null;
        }

        if (!string.IsNullOrEmpty(proxy.Username))
        {
            return new NetworkCredential(proxy.Username, proxy.Password ?? string.Empty);
        }

        if (Uri.TryCreate(proxy.Url.Trim(), UriKind.Absolute, out var address) && !string.IsNullOrEmpty(address.UserInfo))
        {
            var separator = address.UserInfo.IndexOf(':', StringComparison.Ordinal);

            return separator < 0
                ? new NetworkCredential(Uri.UnescapeDataString(address.UserInfo), string.Empty)
                : new NetworkCredential(
                    Uri.UnescapeDataString(address.UserInfo[..separator]),
                    Uri.UnescapeDataString(address.UserInfo[(separator + 1)..]));
        }

        return null;
    }

    private TunerConfig Tuner(string tunerId) =>
        _config.Current.Tuners.FirstOrDefault(tuner => tuner.Id == tunerId)
        ?? throw new TunerException(ProviderId, "tuner.missing", $"There is no tuner '{tunerId}'.", "It was removed. Reload the channel list.");

    private HttpClient Client(TunerConfig tuner, bool stream)
    {
        var address = AddressOf(tuner);
        var credentials = CredentialsOf(tuner);
        var key = $"{tuner.Id}:{(stream ? "stream" : "api")}";

        // Everything that decides where a request goes. A changed proxy is a new client, so the
        // next connection takes the new route without a restart.
        var route = $"{address}|{credentials?.UserName}|{credentials?.Password}";

        lock (_gate)
        {
            if (_clients.TryGetValue(key, out var kept) && kept.Route == route)
            {
                return kept.Client;
            }

            // A running stream may still be reading through the old client, so it is left to the
            // collector rather than disposed under it.
            var client = new HttpClient(Handler(address, credentials, stream), disposeHandler: true)
            {
                Timeout = stream ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(3),
            };

            _clients[key] = (route, client);

            return client;
        }
    }

    private static SocketsHttpHandler Handler(Uri? proxy, NetworkCredential? credentials, bool stream) => new()
    {
        UseProxy = proxy is not null,
        Proxy = proxy is null ? null : new WebProxy(proxy) { Credentials = credentials },
        UseCookies = false,

        // Providers hand streams off to load balancers. Never from an encrypted address to an
        // unencrypted one: the handler refuses that by itself.
        AllowAutoRedirect = true,
        AutomaticDecompression = stream ? DecompressionMethods.None : DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
    };
}
