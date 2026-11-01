using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nightwave.Core.Configuration;
using Nightwave.Core.Tuning;
using Nightwave.Tuners.Iptv.Net;

namespace Nightwave.Tuners.Iptv.HdHomeRun;

/// <summary>What a HDHomeRun says about itself.</summary>
internal sealed record HdHomeRunDevice(string Name, Uri BaseUrl, Uri LineupUrl, int TunerCount);

/// <summary>A channel of a HDHomeRun's lineup.</summary>
internal sealed record HdHomeRunChannel(string Number, string Name, Uri Stream, bool Radio);

/// <summary>
/// The channels of every HDHomeRun, from the lineup the device keeps after its own channel scan.
/// A stream is plain HTTP, each one a tuner of the device, so it is treated as a web stream.
/// </summary>
/// <remarks>
/// Channels under copy protection are left out: the device hands them over encrypted for its
/// own apps only. A guide is not read: on these devices it comes from the maker's paid service.
/// </remarks>
public sealed partial class HdHomeRunTunerProvider : ITunerProvider, IRefreshableTuner
{
    public const string Id = "hdhomerun";

    private static readonly TimeSpan LineupLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan SearchWait = TimeSpan.FromSeconds(2);

    private readonly ConfigStore _config;
    private readonly IptvHttp _http;
    private readonly WebStreams _streams;
    private readonly ILogger<HdHomeRunTunerProvider> _logger;
    private readonly ConcurrentDictionary<string, Kept> _kept = new(StringComparer.Ordinal);

    public HdHomeRunTunerProvider(ConfigStore config, IptvHttp http, WebStreams streams, ILogger<HdHomeRunTunerProvider> logger)
    {
        _config = config;
        _http = http;
        _streams = streams;
        _logger = logger;

        // A changed address is another device.
        _config.Changed += (_, _) => _kept.Clear();
    }

    public string ProviderId => Id;

    public string DisplayNameKey => "provider.hdhomerun";

    public TunerCapabilities Capabilities => TunerCapabilities.StreamSharing | TunerCapabilities.ChannelScan;

    internal IEnumerable<TunerConfig> Tuners =>
        _config.Current.Tuners.Where(tuner => tuner is { Kind: TunerKind.HdHomeRun, Enabled: true });

    public async Task<IReadOnlyList<TvChannel>> GetChannelsAsync(CancellationToken cancellationToken = default)
    {
        var channels = new List<TvChannel>();

        foreach (var tuner in Tuners)
        {
            try
            {
                var kept = await ReadAsync(tuner, cancellationToken).ConfigureAwait(false);

                channels.AddRange(kept.Channels.Select(channel => new TvChannel
                {
                    Id = ChannelId(tuner, channel),
                    ProviderId = Id,
                    Name = channel.Name,
                    Number = channel.Number,
                    Kind = channel.Radio ? ChannelKind.Radio : ChannelKind.Television,
                }));
            }
            catch (Exception error) when (error is TunerException or HttpRequestException or IOException or JsonException or TaskCanceledException)
            {
                // One device being away is no reason for everything else to disappear.
                LogChannelsUnavailable(tuner.Name, error.Message);
            }
        }

        return channels;
    }

    public async Task<IReadOnlyList<TunerStatus>> GetTunersAsync(CancellationToken cancellationToken = default)
    {
        var statuses = new List<TunerStatus>();

        foreach (var tuner in Tuners)
        {
            string? problem = null;
            var limit = tuner.HdHomeRun.MaxStreams;

            try
            {
                var kept = await ReadAsync(tuner, cancellationToken).ConfigureAwait(false);

                limit = Limit(tuner, kept.Device);

                if (kept.Channels.Count == 0)
                {
                    problem = $"{kept.Device.Name} has no channels yet. Run a channel scan.";
                }
            }
            catch (Exception error) when (error is TunerException or HttpRequestException or IOException or JsonException or TaskCanceledException)
            {
                problem = error.Message;
            }

            statuses.Add(new TunerStatus
            {
                TunerId = tuner.Id,
                Name = tuner.Name,
                MaxStreams = limit,
                ActiveStreams = _streams.ActiveOn(tuner.Id),
                Error = problem,
            });
        }

        return statuses;
    }

    public async Task<ITunedStream> OpenAsync(TunedStreamRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tuner = Tuners.FirstOrDefault(candidate => request.ChannelId.StartsWith(candidate.Id + ":", StringComparison.Ordinal))
            ?? throw ChannelMissing(request.ChannelId);
        var kept = await ReadAsync(tuner, cancellationToken).ConfigureAwait(false);
        var channel = kept.Channels.FirstOrDefault(candidate => string.Equals(ChannelId(tuner, candidate), request.ChannelId, StringComparison.Ordinal))
            ?? throw ChannelMissing(request.ChannelId);
        var limit = Limit(tuner, kept.Device);

        return _streams.TryOpen(tuner.Id, request.ChannelId, limit, token => ConnectAsync(tuner, channel, token), StreamClaim.For(request, Id, channel.Name, tuner.Name))
            ?? throw new TunerException(
                Id,
                "tuner.limit_reached",
                $"All {limit} tuners of '{tuner.Name}' are in use.",
                "Stop a running stream, or raise the limit in the tuner settings if the device has more tuners.")
            {
                Values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["tuner"] = tuner.Name, ["limit"] = limit },
            };
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _kept.Clear();

        return Task.CompletedTask;
    }

    /// <summary>Where a device is: the address in its settings, or what answers a search.</summary>
    internal async Task<Uri> AddressOfAsync(TunerConfig tuner, CancellationToken cancellationToken)
    {
        if (tuner.HdHomeRun.Address?.Trim() is { Length: > 0 } address)
        {
            return new Uri(address.Contains("://", StringComparison.Ordinal) ? address : $"http://{address}");
        }

        // This server may be answering such a search itself, and it is not a tuner of its own.
        var own = _config.Current.Output.HdHomeRun.DeviceId;
        var found = (await HdHomeRunProtocol.SearchAsync(SearchWait, cancellationToken).ConfigureAwait(false))
            .Where(device => !string.Equals(device.Answer.DeviceId, own, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (found.Count == 0)
        {
            throw new TunerException(
                Id,
                "tuner.unreachable",
                $"No HDHomeRun answered on the local network for '{tuner.Name}'.",
                "Enter the device's address in the tuner settings. A search does not cross routers or leave a container without host networking.");
        }

        return found[0].Answer.BaseUrl ?? new Uri($"http://{found[0].Address}");
    }

    internal async Task<HdHomeRunDevice> DeviceAsync(TunerConfig tuner, CancellationToken cancellationToken)
    {
        var address = await AddressOfAsync(tuner, cancellationToken).ConfigureAwait(false);
        using var document = await GetJsonAsync(tuner, new Uri(address, "discover.json"), cancellationToken).ConfigureAwait(false);

        return ReadDevice(document.RootElement, address);
    }

    internal async Task<(bool Scanning, int Progress, int Found, IReadOnlyList<string> Sources)> ScanStatusAsync(
        TunerConfig tuner,
        HdHomeRunDevice device,
        CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(tuner, new Uri(device.BaseUrl, "lineup_status.json"), cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        return (
            Number(root, "ScanInProgress") != 0,
            Number(root, "Progress"),
            Number(root, "Found"),
            root.TryGetProperty("SourceList", out var sources) && sources.ValueKind == JsonValueKind.Array
                ? [.. sources.EnumerateArray().Select(source => source.GetString()).OfType<string>()]
                : Text(root, "Source") is { } source ? [source] : []);
    }

    internal async Task PostScanAsync(TunerConfig tuner, HdHomeRunDevice device, string query, CancellationToken cancellationToken)
    {
        using var response = await _http.Api(tuner).PostAsync(new Uri(device.BaseUrl, $"lineup.post?{query}"), null, cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
    }

    internal static HdHomeRunDevice ReadDevice(JsonElement root, Uri address)
    {
        var baseUrl = Uri.TryCreate(Text(root, "BaseURL"), UriKind.Absolute, out var told) ? told : address;

        return new HdHomeRunDevice(
            Text(root, "FriendlyName") ?? Text(root, "ModelNumber") ?? "HDHomeRun",
            baseUrl,
            Uri.TryCreate(Text(root, "LineupURL"), UriKind.Absolute, out var lineup) ? lineup : new Uri(baseUrl, "lineup.json"),
            Math.Max(1, Number(root, "TunerCount")));
    }

    /// <summary>The channels of a lineup that can be played here, and how many cannot.</summary>
    internal static (IReadOnlyList<HdHomeRunChannel> Channels, int Protected) ReadLineup(JsonElement root)
    {
        var channels = new List<HdHomeRunChannel>();
        var protectedChannels = 0;

        foreach (var entry in root.EnumerateArray())
        {
            if (Number(entry, "DRM") != 0)
            {
                protectedChannels++;
                continue;
            }

            if (Text(entry, "GuideNumber") is { } number
                && Uri.TryCreate(Text(entry, "URL"), UriKind.Absolute, out var stream)
                && stream.Scheme == Uri.UriSchemeHttp)
            {
                channels.Add(new HdHomeRunChannel(
                    number,
                    Text(entry, "GuideName") ?? number,
                    stream,
                    entry.TryGetProperty("AudioCodec", out _) && !entry.TryGetProperty("VideoCodec", out _)));
            }
        }

        return (channels, protectedChannels);
    }

    private static string ChannelId(TunerConfig tuner, HdHomeRunChannel channel) => $"{tuner.Id}:{channel.Number}";

    private static int Limit(TunerConfig tuner, HdHomeRunDevice device) =>
        tuner.HdHomeRun.MaxStreams > 0 ? Math.Min(tuner.HdHomeRun.MaxStreams, device.TunerCount) : device.TunerCount;

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    private static TunerException ChannelMissing(string channelId) =>
        new(Id, "tuner.channel_missing", $"Channel '{channelId}' is not in the lineup of any HDHomeRun.", "Run a channel scan, and add the channel to the bouquet again.")
        {
            Values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["channel"] = channelId },
        };

    private async Task<Kept> ReadAsync(TunerConfig tuner, CancellationToken cancellationToken)
    {
        if (_kept.TryGetValue(tuner.Id, out var kept) && DateTimeOffset.UtcNow - kept.At < LineupLifetime)
        {
            return kept;
        }

        var device = await DeviceAsync(tuner, cancellationToken).ConfigureAwait(false);
        using var document = await GetJsonAsync(tuner, device.LineupUrl, cancellationToken).ConfigureAwait(false);
        var (channels, protectedChannels) = ReadLineup(document.RootElement);

        LogLineupLoaded(channels.Count, device.Name, protectedChannels);

        return _kept[tuner.Id] = new Kept(device, channels, DateTimeOffset.UtcNow);
    }

    private async Task<JsonDocument> GetJsonAsync(TunerConfig tuner, Uri address, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.Api(tuner).GetAsync(address, cancellationToken).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            await using (content.ConfigureAwait(false))
            {
                return await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (HttpRequestException error)
        {
            throw new TunerException(
                Id,
                "tuner.unreachable",
                $"'{tuner.Name}' could not be reached at {address.Host}: {error.Message}",
                "Check the address in the tuner settings, and that the device is switched on.");
        }
    }

    private async Task<Stream> ConnectAsync(TunerConfig tuner, HdHomeRunChannel channel, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, channel.Stream);

        HttpResponseMessage response;

        try
        {
            response = await _http.Stream(tuner).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw new TunerException(
                Id,
                "tuner.unreachable",
                $"'{channel.Name}' of '{tuner.Name}' could not be reached: {error.Message}",
                "Check that the device is switched on and on the network.");
        }

        if (!response.IsSuccessStatusCode)
        {
            // The device says in a header of its own what went wrong: no tuner free, no signal.
            var reason = response.Headers.TryGetValues("X-HDHomeRun-Error", out var values) ? string.Join(' ', values) : null;
            var status = (int)response.StatusCode;

            response.Dispose();

            throw new TunerException(
                Id,
                "tuner.stream_refused",
                string.Create(CultureInfo.InvariantCulture, $"'{tuner.Name}' refused '{channel.Name}': HTTP {status}{(reason is null ? string.Empty : $", {reason}")}."),
                "Check that a tuner of the device is free, also from its own apps, and that the channel has signal.")
            {
                Values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["tuner"] = tuner.Name, ["status"] = status },
            };
        }

        return new ResponseStream(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), response);
    }

    [LoggerMessage(EventId = 9940, Level = LogLevel.Warning, Message = "The channels of '{Tuner}' are not available: {Reason}")]
    private partial void LogChannelsUnavailable(string tuner, string reason);

    [LoggerMessage(EventId = 9941, Level = LogLevel.Information, Message = "Read {Count} channels from {Device}; {Protected} under copy protection were left out.")]
    private partial void LogLineupLoaded(int count, string device, int @protected);

    private sealed record Kept(HdHomeRunDevice Device, IReadOnlyList<HdHomeRunChannel> Channels, DateTimeOffset At);
}

/// <summary>
/// A channel scan on the device itself: it keeps the lineup, so this starts the scan, follows it
/// and reads the outcome.
/// </summary>
public sealed class HdHomeRunScanner(HdHomeRunTunerProvider provider) : IChannelScanner
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(1);

    public string ProviderId => HdHomeRunTunerProvider.Id;

    public async Task<IReadOnlyList<ScanTarget>> GetTargetsAsync(CancellationToken cancellationToken = default)
    {
        var channels = await provider.GetChannelsAsync(cancellationToken).ConfigureAwait(false);
        var targets = new List<ScanTarget>();

        foreach (var tuner in provider.Tuners)
        {
            string? problem = null;
            var delivery = DeliverySystem.Network;

            try
            {
                var device = await provider.DeviceAsync(tuner, cancellationToken).ConfigureAwait(false);
                var status = await provider.ScanStatusAsync(tuner, device, cancellationToken).ConfigureAwait(false);

                delivery = (status.Sources.Count > 0 ? status.Sources[0] : null) switch
                {
                    "Cable" => DeliverySystem.Cable,
                    "Antenna" => DeliverySystem.Terrestrial,
                    _ => DeliverySystem.Network,
                };
            }
            catch (Exception error) when (error is TunerException or HttpRequestException or IOException or JsonException or TaskCanceledException)
            {
                problem = error.Message;
            }

            targets.Add(new ScanTarget
            {
                Id = tuner.Id,
                Name = tuner.Name,
                DeliverySystem = delivery,
                KnownChannels = channels.Count(channel => channel.Id.StartsWith(tuner.Id + ":", StringComparison.Ordinal)),
                Error = problem,
            });
        }

        return targets;
    }

    public async Task<ScanResult> ScanAsync(ScanRequest request, IProgress<ScanProgress> progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);

        var tuner = provider.Tuners.FirstOrDefault(candidate => candidate.Id == request.TargetId)
            ?? throw new TunerException(ProviderId, "tuner.missing", $"There is no HDHomeRun '{request.TargetId}'.", "It was removed or switched off.");
        var device = await provider.DeviceAsync(tuner, cancellationToken).ConfigureAwait(false);
        var first = await provider.ScanStatusAsync(tuner, device, cancellationToken).ConfigureAwait(false);
        var source = first.Sources.Count > 0 ? first.Sources[0] : null;

        await provider.PostScanAsync(tuner, device, source is null ? "scan=start" : $"scan=start&source={Uri.EscapeDataString(source)}", cancellationToken).ConfigureAwait(false);

        try
        {
            while (true)
            {
                await Task.Delay(PollEvery, cancellationToken).ConfigureAwait(false);

                var status = await provider.ScanStatusAsync(tuner, device, cancellationToken).ConfigureAwait(false);

                if (!status.Scanning)
                {
                    break;
                }

                progress.Report(new ScanProgress { Fraction = status.Progress / 100d, Current = device.Name, TelevisionFound = status.Found });
            }
        }
        catch (OperationCanceledException)
        {
            await provider.PostScanAsync(tuner, device, "scan=abort", CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        await provider.RefreshAsync(CancellationToken.None).ConfigureAwait(false);

        var channels = await provider.GetChannelsAsync(cancellationToken).ConfigureAwait(false);
        var found = channels.Where(channel => channel.Id.StartsWith(tuner.Id + ":", StringComparison.Ordinal)).ToList();

        return new ScanResult
        {
            TelevisionFound = found.Count(channel => channel.Kind == ChannelKind.Television),
            RadioFound = found.Count(channel => channel.Kind == ChannelKind.Radio),
            EncryptedFound = 0,
        };
    }
}
