using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Nightwave.Core.Configuration;
using Nightwave.Core.Tuning;
using Nightwave.Tuners.Iptv.Guide;
using Nightwave.Tuners.Iptv.Net;

namespace Nightwave.Tuners.Iptv.M3u;

/// <summary>
/// The channels of every M3U tuner. A playlist is fetched, kept for a while and turned into
/// channels; tuning opens the entry's address, shared between whoever watches it.
/// </summary>
public sealed partial class M3uTunerProvider : ITunerProvider, IRefreshableTuner, IWebGuideSource
{
    public const string Id = "m3u";

    private readonly ConfigStore _config;
    private readonly IptvHttp _http;
    private readonly WebStreams _streams;
    private readonly ILogger<M3uTunerProvider> _logger;
    private readonly ConcurrentDictionary<string, Kept> _kept = new(StringComparer.Ordinal);

    public M3uTunerProvider(ConfigStore config, IptvHttp http, WebStreams streams, ILogger<M3uTunerProvider> logger)
    {
        _config = config;
        _http = http;
        _streams = streams;
        _logger = logger;

        // A changed address or proxy is another list.
        _config.Changed += (_, _) => _kept.Clear();
    }

    public string ProviderId => Id;

    public string DisplayNameKey => "provider.m3u";

    public TunerCapabilities Capabilities => TunerCapabilities.StreamSharing;

    private IEnumerable<TunerConfig> Tuners =>
        _config.Current.Tuners.Where(tuner => tuner is { Kind: TunerKind.M3u, Enabled: true });

    public async Task<IReadOnlyList<TvChannel>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        var channels = new List<TvChannel>();

        foreach (var tuner in Tuners)
        {
            try
            {
                var entries = await EntriesAsync(tuner, cancellationToken).ConfigureAwait(false);

                channels.AddRange(entries.Select(entry => new TvChannel
                {
                    Id = ChannelId(tuner, entry),
                    ProviderId = Id,
                    Name = entry.Name,
                    Number = entry.Number,
                    Kind = entry.IsRadio ? ChannelKind.Radio : ChannelKind.Television,
                    Logo = entry.Logo,
                    GuideId = entry.GuideId is { Length: > 0 } guide ? GuideId(tuner.Id, guide) : null,
                }));
            }
            catch (Exception error) when (error is NightwaveException or HttpRequestException or IOException or TaskCanceledException)
            {
                // One playlist being down is no reason for the others' channels to disappear.
                LogChannelsUnavailable(tuner.Name, error.Message);
            }
        }

        return channels;
    }

    public Task<IReadOnlyList<TunerStatus>> GetTunersAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TunerStatus>>(
        [
            .. Tuners.Select(tuner => new TunerStatus
            {
                TunerId = tuner.Id,
                Name = tuner.Name,
                MaxStreams = tuner.M3u.MaxStreams,
                ActiveStreams = _streams.ActiveOn(tuner.Id),
                Error = string.IsNullOrWhiteSpace(tuner.M3u.PlaylistUrl) ? "No playlist address is set for this tuner." : null,
            }),
        ]);

    public async Task<ITunedStream> OpenAsync(TunedStreamRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tuner = Tuners.FirstOrDefault(candidate => request.ChannelId.StartsWith(candidate.Id + ":", StringComparison.Ordinal))
            ?? throw ChannelMissing(request.ChannelId);

        var entries = await EntriesAsync(tuner, cancellationToken).ConfigureAwait(false);
        var entry = entries.FirstOrDefault(candidate => string.Equals(ChannelId(tuner, candidate), request.ChannelId, StringComparison.OrdinalIgnoreCase))
            ?? throw ChannelMissing(request.ChannelId);

        if (!IsByteStream(entry.StreamUrl))
        {
            // A playlist of segments or another protocol is nothing a byte relay can carry. The
            // address goes to the client as it is, which a proxy would not cover.
            if (tuner.EffectiveProxy is not null)
            {
                throw new TunerException(
                    Id,
                    "tuner.proxy_needs_byte_stream",
                    $"'{entry.Name}' on '{tuner.Name}' is not a plain HTTP transport stream ({entry.StreamUrl.Scheme}, {Path.GetExtension(entry.StreamUrl.AbsolutePath)}), so it cannot go through the tuner's proxy, and nothing was sent without it.",
                    "Use a provider address that delivers MPEG-TS, or switch the proxy off for this tuner.")
                {
                    Values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["channel"] = entry.Name, ["tuner"] = tuner.Name },
                };
            }

            return new RedirectedStream(request.ChannelId, entry.StreamUrl);
        }

        var stream = _streams.TryOpen(tuner.Id, request.ChannelId, tuner.M3u.MaxStreams, token => ConnectAsync(tuner, entry, token), StreamClaim.For(request, Id, entry.Name, tuner.Name))
            ?? throw new TunerException(
                Id,
                "tuner.limit_reached",
                $"'{tuner.Name}' allows {tuner.M3u.MaxStreams} streams at once and all of them are in use.",
                "Stop a running stream, or raise the limit in the tuner settings if the provider allows it.")
            {
                Values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["tuner"] = tuner.Name, ["limit"] = tuner.M3u.MaxStreams },
            };

        LogChannelOpened(entry.Name, tuner.Name, request.ClientDeviceId);

        return stream;
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _kept.Clear();

        return Task.CompletedTask;
    }

    async Task<IReadOnlyList<WebGuideFeed>> IWebGuideSource.FeedsAsync(CancellationToken cancellationToken)
    {
        var feeds = new List<WebGuideFeed>();

        foreach (var tuner in Tuners.Where(tuner => !string.IsNullOrWhiteSpace(tuner.M3u.XmltvUrl)))
        {
            var entries = await EntriesAsync(tuner, cancellationToken).ConfigureAwait(false);

            feeds.Add(new WebGuideFeed(
                tuner.Id,
                $"'{tuner.Name}'",
                token => OpenGuideAsync(tuner, token),
                entries.Select(entry => entry.GuideId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase),
                id => GuideId(tuner.Id, id)));
        }

        return feeds;
    }

    private static string ChannelId(TunerConfig tuner, M3uEntry entry) => $"{tuner.Id}:{entry.Id}";

    private static string GuideId(string tunerId, string id) => $"{tunerId}:{id}";

    private static bool IsByteStream(Uri address) =>
        address.Scheme is "http" or "https"
        && !address.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    private static bool IsLocalPath(string address) =>
        !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https");

    private static TunerException ChannelMissing(string channelId) =>
        new(Id, "tuner.channel_missing", $"Channel '{channelId}' is not in any M3U playlist.", "Refresh the tuner, or check that the channel still exists at the provider.")
        {
            Values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["channel"] = channelId },
        };

    private async Task<IReadOnlyList<M3uEntry>> EntriesAsync(TunerConfig tuner, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tuner.M3u.PlaylistUrl))
        {
            return [];
        }

        var lifetime = TimeSpan.FromHours(Math.Max(1, tuner.M3u.RefreshHours));

        if (_kept.TryGetValue(tuner.Id, out var kept) && DateTimeOffset.UtcNow - kept.At < lifetime)
        {
            return kept.Entries;
        }

        var address = tuner.M3u.PlaylistUrl.Trim();
        var content = IsLocalPath(address)
            ? await File.ReadAllTextAsync(address, cancellationToken).ConfigureAwait(false)
            : await SendForTextAsync(tuner, address, cancellationToken).ConfigureAwait(false);
        var entries = M3uPlaylist.Parse(content);

        _kept[tuner.Id] = new Kept(entries, DateTimeOffset.UtcNow);
        LogPlaylistLoaded(entries.Count, tuner.Name);

        return entries;
    }

    private async Task<string> SendForTextAsync(TunerConfig tuner, string address, CancellationToken cancellationToken)
    {
        using var request = Request(tuner, new Uri(address));
        using var response = await _http.Api(tuner).SendAsync(request, cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Stream> OpenGuideAsync(TunerConfig tuner, CancellationToken cancellationToken)
    {
        var address = tuner.M3u.XmltvUrl!.Trim();

        if (IsLocalPath(address))
        {
            return File.OpenRead(address);
        }

        return await OpenAsync(tuner, new Uri(address), _http.Api(tuner), "the guide", cancellationToken).ConfigureAwait(false);
    }

    private Task<Stream> ConnectAsync(TunerConfig tuner, M3uEntry entry, CancellationToken cancellationToken) =>
        OpenAsync(tuner, entry.StreamUrl, _http.Stream(tuner), $"'{entry.Name}'", cancellationToken);

    private static async Task<Stream> OpenAsync(TunerConfig tuner, Uri address, HttpClient client, string what, CancellationToken cancellationToken)
    {
        using var request = Request(tuner, address);

        HttpResponseMessage response;

        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw new TunerException(
                Id,
                "tuner.unreachable",
                $"{what} of '{tuner.Name}' could not be reached: {error.Message}",
                tuner.EffectiveProxy is null
                    ? "Check the address in the tuner settings, and that this server can reach the provider."
                    : "Check the address in the tuner settings, and that the proxy is running and can reach the provider.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;

            response.Dispose();

            throw new TunerException(
                Id,
                "tuner.stream_refused",
                $"The provider of '{tuner.Name}' refused {what}: HTTP {status}.",
                "Check with the provider that the subscription is active and includes this channel, then refresh the tuner: its addresses may have changed.")
            {
                Values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["tuner"] = tuner.Name, ["status"] = status },
            };
        }

        return new ResponseStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response);
    }

    private static HttpRequestMessage Request(TunerConfig tuner, Uri address)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, address);

        request.Headers.TryAddWithoutValidation("User-Agent", string.IsNullOrWhiteSpace(tuner.M3u.UserAgent) ? "Nightwave" : tuner.M3u.UserAgent.Trim());

        return request;
    }

    private sealed record Kept(IReadOnlyList<M3uEntry> Entries, DateTimeOffset At);

    [LoggerMessage(EventId = 310, Level = LogLevel.Information, Message = "Loaded {Count} channels from the playlist of tuner '{Tuner}'.")]
    private partial void LogPlaylistLoaded(int count, string tuner);

    [LoggerMessage(EventId = 311, Level = LogLevel.Information, Message = "Opened channel '{Channel}' on '{Tuner}' for {Client}.")]
    private partial void LogChannelOpened(string channel, string tuner, string client);

    [LoggerMessage(EventId = 312, Level = LogLevel.Warning, Message = "The channels of tuner '{Tuner}' are left out: {Reason}")]
    private partial void LogChannelsUnavailable(string tuner, string reason);
}
