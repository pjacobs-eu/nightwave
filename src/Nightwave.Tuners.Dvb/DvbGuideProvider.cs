using System.Globalization;
using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb.Epg;

namespace Nightwave.Tuners.Dvb;

/// <summary>
/// The guide the broadcast carries: DVB EIT, read while tuners stream and by the grabber while
/// they are idle. Programmes are keyed by the DVB service - the <c>GuideId</c> the tuner gives
/// its channels - so the guide matches whichever device received it.
/// </summary>
internal sealed class DvbGuideProvider : IGuideProvider
{
    private readonly EpgService _epg;
    private readonly EpgGrabber _grabber;

    public DvbGuideProvider(EpgService epg, EpgGrabber grabber)
    {
        _epg = epg;
        _grabber = grabber;
    }

    public string ProviderId => DvbProvider.Id;

    public string DisplayNameKey => "provider.satip.guide.name";

    public async Task<IReadOnlyList<string>> GetGuideChannelIdsAsync(CancellationToken cancellationToken = default)
    {
        await _epg.Store.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        return [.. _epg.Store.Services().Select(service => service.GuideId)];
    }

    public async Task<IReadOnlyList<Programme>> GetProgramsAsync(
        IReadOnlyList<string> guideChannelIds,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(guideChannelIds);

        await _epg.Store.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        var programs = new List<Programme>();

        foreach (var guideId in guideChannelIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!DvbServiceKey.TryParse(guideId, out var service))
            {
                continue;
            }

            programs.AddRange(_epg.Store.Query(service, from, to).Select(programme => new Programme
            {
                Id = string.Create(CultureInfo.InvariantCulture, $"{guideId}:{programme.EventId}:{programme.Start.ToUnixTimeSeconds()}"),
                ChannelId = guideId,
                Title = programme.Title,
                EpisodeTitle = programme.Subtitle,
                Description = programme.Description,
                StartsAt = programme.Start,
                EndsAt = programme.End,
                Genres = programme.Genres,
                IsLive = programme.IsLive,
            }));
        }

        return programs;
    }

    /// <summary>Starts a grabber run and returns; the run takes minutes, and its programmes appear as they arrive.</summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _grabber.Trigger();

        return Task.CompletedTask;
    }
}
