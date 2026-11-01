namespace Nightwave.Core.Tuning;

/// <summary>
/// Supplies programme guide data. Kept separate from the tuner: an IPTV playlist and an
/// XMLTV guide are usually two different plugins, matched over
/// <see cref="TvChannel.GuideId"/>.
/// </summary>
public interface IGuideProvider
{
    string ProviderId { get; }

    /// <summary>Translation key for the name shown in the UI.</summary>
    string DisplayNameKey { get; }

    /// <summary>Channel ids this provider has guide data for.</summary>
    Task<IReadOnlyList<string>> GetGuideChannelIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Programmes for the given guide channels in the given window. Implementations return
    /// what they have; a gap in the guide is not an error.
    /// </summary>
    Task<IReadOnlyList<Programme>> GetProgramsAsync(
        IReadOnlyList<string> guideChannelIds,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken = default);

    /// <summary>Pulls fresh guide data from the upstream source, as scheduled by the server.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
