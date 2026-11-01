using System.Globalization;
using System.Text;
using System.Xml;
using Nightwave.Core.Lineup;
using Nightwave.Core.Tuning;

namespace Nightwave.Core.Output;

/// <summary>Where a client finds things on this server. Every address carries the access token.</summary>
public sealed record OutputLinks(Uri BaseUrl, string? Token)
{
    public Uri Playlist(Guid bouquetId) => Link($"output/{bouquetId:N}/playlist.m3u");

    public Uri Guide(Guid bouquetId) => Link($"output/{bouquetId:N}/guide.xml");

    public Uri Stream(ChannelKey key) => Link($"stream/{key?.Encoded}");

    public Uri Logo(ChannelKey key) => Link($"logo/{key?.Encoded}");

    private Uri Link(string path) => new UriBuilder(new Uri(BaseUrl, path))
    {
        Query = string.IsNullOrEmpty(Token) ? string.Empty : $"token={Uri.EscapeDataString(Token)}",
    }.Uri;
}

/// <summary>Writes a bouquet as an extended M3U playlist.</summary>
public static class PlaylistWriter
{
    /// <param name="hasOwnLogo">Whether a tuner carries the channel's logo, so it is served from here.</param>
    public static string Write(Bouquet bouquet, IReadOnlyList<NumberedChannel> channels, OutputLinks links, Func<ChannelKey, bool> hasOwnLogo)
    {
        ArgumentNullException.ThrowIfNull(bouquet);
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(hasOwnLogo);

        var text = new StringBuilder();

        text.Append(CultureInfo.InvariantCulture, $"#EXTM3U url-tvg=\"{links.Guide(bouquet.Id)}\"\n");

        foreach (var entry in channels)
        {
            var logo = hasOwnLogo(entry.Key) ? links.Logo(entry.Key) : entry.Channel.Logo;

            text.Append(CultureInfo.InvariantCulture, $"#EXTINF:-1 tvg-id=\"{Attribute(entry.GuideKey)}\"");
            text.Append(CultureInfo.InvariantCulture, $" tvg-name=\"{Attribute(entry.Channel.Name)}\"");
            text.Append(CultureInfo.InvariantCulture, $" tvg-chno=\"{entry.Number}\"");

            if (logo is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $" tvg-logo=\"{Attribute(logo.ToString())}\"");
            }

            if (entry.Channel.Kind == ChannelKind.Radio)
            {
                text.Append(" radio=\"true\"");
            }

            text.Append(CultureInfo.InvariantCulture, $" group-title=\"{Attribute(bouquet.Name)}\",{Line(entry.Channel.Name)}\n");
            text.Append(links.Stream(entry.Key)).Append('\n');
        }

        return text.ToString();
    }

    private static string Attribute(string value) => Line(value).Replace('"', '\'');

    /// <summary>A name from a broadcast or a provider has no business starting a new line in a playlist.</summary>
    private static string Line(string value) => value.ReplaceLineEndings(" ");
}

/// <summary>Writes a bouquet's guide as XMLTV.</summary>
public static class XmltvWriter
{
    public static async Task WriteAsync(
        Stream output,
        IReadOnlyList<NumberedChannel> channels,
        ILookup<string, Programme> programmes,
        OutputLinks links,
        Func<ChannelKey, bool> hasOwnLogo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);
        ArgumentNullException.ThrowIfNull(programmes);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(hasOwnLogo);

        var writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Async = true,
            Indent = true,
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            CheckCharacters = false,
        });

        await using (writer.ConfigureAwait(false))
        {
            await writer.WriteStartDocumentAsync().ConfigureAwait(false);
            await writer.WriteStartElementAsync(null, "tv", null).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "generator-info-name", null, "Nightwave").ConfigureAwait(false);

            var distinct = channels.DistinctBy(channel => channel.GuideKey, StringComparer.OrdinalIgnoreCase).ToList();

            foreach (var entry in distinct)
            {
                await writer.WriteStartElementAsync(null, "channel", null).ConfigureAwait(false);
                await writer.WriteAttributeStringAsync(null, "id", null, entry.GuideKey).ConfigureAwait(false);
                await writer.WriteElementStringAsync(null, "display-name", null, Clean(entry.Channel.Name)).ConfigureAwait(false);

                if ((hasOwnLogo(entry.Key) ? links.Logo(entry.Key) : entry.Channel.Logo) is { } logo)
                {
                    await writer.WriteStartElementAsync(null, "icon", null).ConfigureAwait(false);
                    await writer.WriteAttributeStringAsync(null, "src", null, logo.ToString()).ConfigureAwait(false);
                    await writer.WriteEndElementAsync().ConfigureAwait(false);
                }

                await writer.WriteEndElementAsync().ConfigureAwait(false);
            }

            foreach (var entry in distinct.Where(channel => channel.Channel.GuideId is not null))
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var programme in programmes[entry.Channel.GuideId!].OrderBy(programme => programme.StartsAt))
                {
                    await WriteProgrammeAsync(writer, entry.GuideKey, programme).ConfigureAwait(false);
                }
            }

            await writer.WriteEndElementAsync().ConfigureAwait(false);
            await writer.WriteEndDocumentAsync().ConfigureAwait(false);
        }
    }

    private static async Task WriteProgrammeAsync(XmlWriter writer, string channel, Programme programme)
    {
        await writer.WriteStartElementAsync(null, "programme", null).ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "start", null, Time(programme.StartsAt)).ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "stop", null, Time(programme.EndsAt)).ConfigureAwait(false);
        await writer.WriteAttributeStringAsync(null, "channel", null, channel).ConfigureAwait(false);
        await writer.WriteElementStringAsync(null, "title", null, Clean(programme.Title)).ConfigureAwait(false);

        if (programme.EpisodeTitle is { } subtitle)
        {
            await writer.WriteElementStringAsync(null, "sub-title", null, Clean(subtitle)).ConfigureAwait(false);
        }

        if (programme.Description is { } description)
        {
            await writer.WriteElementStringAsync(null, "desc", null, Clean(description)).ConfigureAwait(false);
        }

        foreach (var genre in programme.Genres)
        {
            await writer.WriteElementStringAsync(null, "category", null, Clean(genre)).ConfigureAwait(false);
        }

        if (programme.SeasonNumber is { } season && programme.EpisodeNumber is { } episode)
        {
            await writer.WriteStartElementAsync(null, "episode-num", null).ConfigureAwait(false);
            await writer.WriteAttributeStringAsync(null, "system", null, "xmltv_ns").ConfigureAwait(false);
            await writer.WriteStringAsync(string.Create(CultureInfo.InvariantCulture, $"{season - 1}.{episode - 1}.")).ConfigureAwait(false);
            await writer.WriteEndElementAsync().ConfigureAwait(false);
        }

        if (programme.IsLive)
        {
            await writer.WriteElementStringAsync(null, "live", null, string.Empty).ConfigureAwait(false);
        }

        await writer.WriteEndElementAsync().ConfigureAwait(false);
    }

    private static string Time(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyyMMddHHmmss '+0000'", CultureInfo.InvariantCulture);

    /// <summary>Broadcast text can hold control characters XML has no way of writing.</summary>
    private static string Clean(string text)
    {
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (var index = 0; index < source.Length; index++)
            {
                var character = source[index];

                span[index] = XmlConvert.IsXmlChar(character) || char.IsSurrogate(character) ? character : ' ';
            }
        });
    }
}
