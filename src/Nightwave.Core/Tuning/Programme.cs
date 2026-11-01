namespace Nightwave.Core.Tuning;

/// <summary>One programme in the electronic programme guide.</summary>
public sealed record Programme
{
    public required string Id { get; init; }

    /// <summary>Channel this programme airs on, matching <see cref="TvChannel.Id"/>.</summary>
    public required string ChannelId { get; init; }

    public required string Title { get; init; }

    public required DateTimeOffset StartsAt { get; init; }

    public required DateTimeOffset EndsAt { get; init; }

    public string? EpisodeTitle { get; init; }

    public string? Description { get; init; }

    public int? SeasonNumber { get; init; }

    public int? EpisodeNumber { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>Original broadcast date, where the guide states one. Distinguishes a repeat from a premiere.</summary>
    public DateOnly? OriginalAirDate { get; init; }

    public bool IsLive { get; init; }

    public Uri? Image { get; init; }
}
