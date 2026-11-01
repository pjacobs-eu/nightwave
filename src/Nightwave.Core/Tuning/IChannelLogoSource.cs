namespace Nightwave.Core.Tuning;

/// <summary>
/// A tuner provider that carries the logos of its channels itself rather than pointing at them
/// somewhere on the internet - bundled with the plugin, or read off the broadcast. Implemented by
/// an <see cref="ITunerProvider"/> next to that interface; the server then gives each
/// channel with a logo a <see cref="TvChannel.Logo"/> of its own and serves the picture.
/// </summary>
/// <remarks>
/// Asked once per channel whenever the channel list is put together, so both methods answer from
/// memory and do no I/O.
/// </remarks>
public interface IChannelLogoSource
{
    /// <summary>Whether there is a logo for the channel, in either style.</summary>
    bool HasLogo(string channelId);

    /// <summary>
    /// The logo of a channel drawn for the given background, or the other style when that is the
    /// only one there is. Null when the channel has none.
    /// </summary>
    ChannelLogo? FindLogo(string channelId, LogoBackground background);
}

/// <summary>A channel logo as bytes.</summary>
/// <param name="Content">The picture.</param>
/// <param name="ContentType">Its media type, for example <c>image/png</c>.</param>
/// <param name="Tag">Changes whenever the picture does; the server's ETag.</param>
public sealed record ChannelLogo(ReadOnlyMemory<byte> Content, string ContentType, string Tag);

/// <summary>
/// What a logo is drawn on. Channel logos come in two styles because a white wordmark vanishes on
/// a light page and a black one on a dark page.
/// </summary>
public enum LogoBackground
{
    Dark,
    Light,
}
