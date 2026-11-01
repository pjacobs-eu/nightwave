using Microsoft.Extensions.Logging;
using Nightwave.Core.Tuning;

namespace Nightwave.Core.Lineup;

/// <summary>Every channel of every tuner, and what is known about a bouquet's channels.</summary>
public sealed partial class LineupService
{
    private readonly IReadOnlyList<ITunerProvider> _providers;
    private readonly IReadOnlyList<IGuideProvider> _guides;
    private readonly ILogger<LineupService> _logger;
    private readonly ChannelLogoStore? _logos;

    public LineupService(IEnumerable<ITunerProvider> providers, IEnumerable<IGuideProvider> guides, ILogger<LineupService> logger, ChannelLogoStore? logos = null)
    {
        _logos = logos;
        _providers = [.. providers];
        _guides = [.. guides];
        _logger = logger;
    }

    public IReadOnlyList<ITunerProvider> Providers => _providers;

    public ITunerProvider? Provider(string providerId) =>
        _providers.FirstOrDefault(provider => string.Equals(provider.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));

    /// <summary>All channels there are. A provider that cannot answer leaves its own out, and only those.</summary>
    public async Task<IReadOnlyList<TvChannel>> ChannelsAsync(CancellationToken cancellationToken = default)
    {
        var lists = await Task.WhenAll(_providers.Select(provider => ChannelsOfAsync(provider, cancellationToken))).ConfigureAwait(false);

        return [.. lists.SelectMany(list => list).Select(WithChosenLogo)];
    }

    /// <summary>A bouquet's channels in order, numbered from one; channels that are gone are left out.</summary>
    public async Task<IReadOnlyList<NumberedChannel>> ResolveAsync(Bouquet bouquet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bouquet);

        var known = (await ChannelsAsync(cancellationToken).ConfigureAwait(false))
            .GroupBy(channel => new ChannelKey(channel.ProviderId, channel.Id))
            .ToDictionary(group => group.Key, group => group.First());

        var resolved = new List<NumberedChannel>(bouquet.Channels.Count);

        for (var index = 0; index < bouquet.Channels.Count; index++)
        {
            if (known.TryGetValue(bouquet.Channels[index], out var channel))
            {
                resolved.Add(new NumberedChannel(index + 1, channel));
            }
        }

        return resolved;
    }

    /// <summary>Whether a channel's logo is served from here: a picture kept here, or a tuner's own.</summary>
    public bool ServesLogo(ChannelKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return _logos?.Find(key)?.Mode switch
        {
            ChannelLogoMode.File => true,
            ChannelLogoMode.Removed or ChannelLogoMode.Address => false,
            _ => LogoSource(key.ProviderId)?.HasLogo(key.ChannelId) == true,
        };
    }

    /// <summary>The picture served for a channel, where one is.</summary>
    public ChannelLogo? FindLogo(ChannelKey key, LogoBackground background)
    {
        ArgumentNullException.ThrowIfNull(key);

        return _logos?.Find(key) switch
        {
            { Mode: ChannelLogoMode.File, Picture: { } picture } => picture,
            null => LogoSource(key.ProviderId)?.FindLogo(key.ChannelId, background),
            _ => null,
        };
    }

    /// <summary>The logo a tuner carries itself, where it does.</summary>
    private IChannelLogoSource? LogoSource(string providerId) => Provider(providerId) as IChannelLogoSource;

    /// <summary>A channel as it goes out: a logo chosen by hand goes before what its tuner offers.</summary>
    private TvChannel WithChosenLogo(TvChannel channel) => _logos?.Find(new ChannelKey(channel.ProviderId, channel.Id)) switch
    {
        null => channel,
        { Mode: ChannelLogoMode.Address } chosen => channel with { Logo = chosen.Address },
        _ => channel with { Logo = null },
    };

    /// <summary>The programmes of these channels within a window, grouped by guide id.</summary>
    public async Task<ILookup<string, Programme>> ProgrammesAsync(
        IReadOnlyCollection<TvChannel> channels,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);

        var programmes = new List<Programme>();

        foreach (var group in channels.Where(channel => channel.GuideId is not null).GroupBy(channel => channel.ProviderId, StringComparer.OrdinalIgnoreCase))
        {
            var ids = group.Select(channel => channel.GuideId!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var guide in _guides.Where(guide => string.Equals(guide.ProviderId, group.Key, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    programmes.AddRange(await guide.GetProgramsAsync(ids, from, to, cancellationToken).ConfigureAwait(false));
                }
                catch (NightwaveException error)
                {
                    // A gap in the guide is not worth losing the rest of it for.
                    LogGuideUnavailable(group.Key, error.Message);
                }
            }
        }

        return programmes.ToLookup(programme => programme.ChannelId, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<TvChannel>> ChannelsOfAsync(ITunerProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            return await provider.GetChannelsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is NightwaveException or IOException or HttpRequestException)
        {
            LogChannelsUnavailable(provider.ProviderId, error.Message);

            return [];
        }
    }

    [LoggerMessage(EventId = 100, Level = LogLevel.Warning, Message = "The channels of {Provider} are left out: {Reason}")]
    private partial void LogChannelsUnavailable(string provider, string reason);

    [LoggerMessage(EventId = 101, Level = LogLevel.Warning, Message = "The guide of {Provider} is left out: {Reason}")]
    private partial void LogGuideUnavailable(string provider, string reason);
}

/// <summary>A channel with its number in a bouquet.</summary>
public sealed record NumberedChannel(int Number, TvChannel Channel)
{
    public ChannelKey Key => new(Channel.ProviderId, Channel.Id);

    /// <summary>What ties playlist and guide together: the guide id, or the channel's own key.</summary>
    public string GuideKey => Channel.GuideId ?? Key.Encoded;
}
