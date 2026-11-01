using System.Xml;
using Microsoft.Extensions.Logging;
using Nightwave.Core.Tuning;

namespace Nightwave.Tuners.Iptv.Guide;

/// <summary>Where a web tuner's guide comes from, as one kind of tuner knows it.</summary>
public interface IWebGuideSource
{
    string ProviderId { get; }

    /// <summary>The tuners with a guide right now, and the guide ids their channels use.</summary>
    Task<IReadOnlyList<WebGuideFeed>> FeedsAsync(CancellationToken cancellationToken);
}

/// <param name="Open">Opens the feed.</param>
/// <param name="Offered">The feed's own channel ids that are offered.</param>
/// <param name="GuideId">Makes a feed's channel id the tuner's own.</param>
public sealed record WebGuideFeed(
    string TunerId,
    string Label,
    Func<CancellationToken, Task<Stream>> Open,
    IReadOnlySet<string> Offered,
    Func<string, string> GuideId);

/// <summary>
/// Programme data from the XMLTV feeds of web tuners, fetched in the background and answered from
/// the database: a client asking for a guide never waits for a provider's feed.
/// </summary>
public sealed partial class WebGuideProvider : IGuideProvider, IBackgroundWork, IDisposable
{
    /// <summary>How long a feed is kept before it is fetched again. It is big, and it changes slowly.</summary>
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);

    /// <summary>How often feeds are checked for being due.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    /// <summary>How long a feed that failed is left alone.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(15);

    private readonly IWebGuideSource _source;
    private readonly WebGuideStore _store;
    private readonly ILogger<WebGuideProvider> _logger;

    // One feed at a time: several at once is several hundred megabytes of XML at once.
    private readonly SemaphoreSlim _fetching = new(1, 1);
    private CancellationTokenSource? _stop;
    private Task? _loop;

    public WebGuideProvider(IWebGuideSource source, WebGuideStore store, ILogger<WebGuideProvider> logger)
    {
        _source = source;
        _store = store;
        _logger = logger;
    }

    public string ProviderId => _source.ProviderId;

    public string DisplayNameKey => $"provider.{_source.ProviderId}.guide";

    public void Dispose()
    {
        _stop?.Dispose();
        _fetching.Dispose();
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_stop.Token), CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_stop is not null && _loop is not null)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            await _loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public Task<IReadOnlyList<string>> GetGuideChannelIdsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<Programme>> GetProgramsAsync(
        IReadOnlyList<string> guideChannelIds,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(guideChannelIds);

        return Task.FromResult<IReadOnlyList<Programme>>([.. guideChannelIds.SelectMany(id => _store.Query(id, from, to))]);
    }

    public Task RefreshAsync(CancellationToken cancellationToken = default) => FetchAsync(force: true, cancellationToken);

    private async Task LoopAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(CheckInterval);

        do
        {
            try
            {
                await FetchAsync(force: false, stop).ConfigureAwait(false);
            }
            catch (Exception error) when (error is NightwaveException or IOException or HttpRequestException)
            {
                LogGuideUnavailable(ProviderId, error.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false));
    }

    private async Task FetchAsync(bool force, CancellationToken cancellationToken)
    {
        await _fetching.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            foreach (var feed in await _source.FeedsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (force || _store.FetchedAt(feed.TunerId) is not { } at || DateTimeOffset.UtcNow - at >= Lifetime)
                {
                    await FetchAsync(feed, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _fetching.Release();
        }
    }

    private async Task FetchAsync(WebGuideFeed feed, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await feed.Open(cancellationToken).ConfigureAwait(false);

            await using (stream.ConfigureAwait(false))
            {
                var programmes = await XmltvReader.ReadAsync(stream, feed.GuideId, feed.Offered, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

                _store.Replace(feed.TunerId, programmes, DateTimeOffset.UtcNow);
                LogGuideLoaded(programmes.Count, feed.Label);
            }
        }
        catch (Exception error) when (error is NightwaveException or XmlException or HttpRequestException or IOException)
        {
            // What was there stays, and is asked for again in a while rather than on every read.
            LogGuideUnavailable(feed.Label, error.Message);
            _store.MarkFetched(feed.TunerId, DateTimeOffset.UtcNow - Lifetime + RetryAfter);
        }
    }

    [LoggerMessage(EventId = 300, Level = LogLevel.Information, Message = "Loaded {Count} guide entries for the channels of {Tuner}.")]
    private partial void LogGuideLoaded(int count, string tuner);

    [LoggerMessage(EventId = 301, Level = LogLevel.Warning, Message = "The guide of {Tuner} could not be loaded; what was there before is kept: {Reason}")]
    private partial void LogGuideUnavailable(string tuner, string reason);
}
