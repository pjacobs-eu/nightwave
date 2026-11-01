
namespace Nightwave.Core.Tuning;

/// <summary>
/// A kind of channel source: SAT>IP servers, M3U playlists, DVB cards, HDHomeRun devices.
/// </summary>
public interface ITunerProvider
{
    /// <summary>Stable identifier, used in channel keys and stored bouquets, so it never changes.</summary>
    string ProviderId { get; }

    /// <summary>Translation key for the name shown in the UI.</summary>
    string DisplayNameKey { get; }

    TunerCapabilities Capabilities { get; }

    /// <summary>Channels this provider currently offers.</summary>
    Task<IReadOnlyList<TvChannel>> GetChannelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Tuners and how busy they are, for the admin UI and for capacity checks.</summary>
    Task<IReadOnlyList<TunerStatus>> GetTunersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tunes a channel and returns a handle to its stream. Fails with an explicit error when
    /// no tuner is free - it never waits silently for one to become available.
    /// </summary>
    Task<ITunedStream> OpenAsync(TunedStreamRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What a tuner provider supports beyond plain tuning.</summary>
[Flags]
public enum TunerCapabilities
{
    None = 0,

    /// <summary>One tuner can serve several clients watching the same channel.</summary>
    StreamSharing = 1 << 1,

    /// <summary>The provider can scan for channels rather than only listing configured ones.</summary>
    ChannelScan = 1 << 2,

}

/// <summary>A request to tune a channel.</summary>
public sealed record TunedStreamRequest
{
    public required string ChannelId { get; init; }

    /// <summary>Client the stream is for, used for tuner accounting and session records.</summary>
    public required string ClientDeviceId { get; init; }

    /// <summary>
    /// Whether this stream may take the place of the client's one running stream when nothing
    /// else is free, as a player changing channel needs. <see cref="ClientDeviceId"/> has to tell
    /// clients apart for this.
    /// </summary>
    public bool ReplacesOwnStream { get; init; }
}

/// <summary>An open channel. Disposing it gives the tuner back.</summary>
public interface ITunedStream : IAsyncDisposable
{
    Guid Id { get; }

    string ChannelId { get; }

    /// <summary>True while the tuner is delivering data.</summary>
    bool IsActive { get; }

    /// <summary>
    /// Where a client has to go itself, for a source that cannot be relayed as one byte stream:
    /// a web playlist of segments. Null everywhere else.
    /// </summary>
    Uri? DirectAddress => null;

    /// <summary>Opens the live data. Live streams are not seekable unless the provider timeshifts.</summary>
    Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default);
}

/// <summary>State of one physical or logical tuner.</summary>
public sealed record TunerStatus
{
    public required string TunerId { get; init; }

    public required string Name { get; init; }

    public required int MaxStreams { get; init; }

    public required int ActiveStreams { get; init; }

    /// <summary>Channel currently tuned, when the tuner is busy.</summary>
    public string? ActiveChannelId { get; init; }

    /// <summary>English, untranslated reason when the tuner is unusable.</summary>
    public string? Error { get; init; }
}

/// <summary>A tuner whose channels come from a list that can be fetched anew on request.</summary>
public interface IRefreshableTuner
{
    Task RefreshAsync(CancellationToken cancellationToken = default);
}
