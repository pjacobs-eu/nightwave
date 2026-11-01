using System.Globalization;
using System.Xml;
using Nightwave.Core.Tuning;

namespace Nightwave.Tuners.Iptv.Guide;

/// <summary>
/// Reads an XMLTV feed as it arrives. A provider's feed covers every channel it has, far more
/// than anybody selected and far more days than anybody looks at, so only what is wanted is kept.
/// </summary>
public static class XmltvReader
{
    /// <summary>How far back programmes are kept - far enough for what is on now to have started.</summary>
    private static readonly TimeSpan KeepBehind = TimeSpan.FromHours(12);

    /// <summary>How far ahead they are kept.</summary>
    private static readonly TimeSpan KeepAhead = TimeSpan.FromDays(8);

    /// <summary>
    /// The programmes in a feed that belong to offered channels and fall into the window around
    /// <paramref name="now"/>, with their channel ids made the tuner's own.
    /// </summary>
    public static async Task<IReadOnlyList<Programme>> ReadAsync(
        Stream feed,
        Func<string, string> guideId,
        IReadOnlySet<string> offeredChannels,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var programs = new List<Programme>();
        var from = now - KeepBehind;
        var to = now + KeepAhead;

        using var reader = XmlReader.Create(feed, new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreWhitespace = true,
        });

        // Skipping an element already moves onto the next one, so the loop only reads on where
        // nothing else did - reading on after a skip would step over a programme every time.
        var more = await reader.ReadAsync().ConfigureAwait(false);

        while (more)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (reader.NodeType == XmlNodeType.Element && reader.Name == "programme")
            {
                var channel = reader.GetAttribute("channel");
                var start = ParseTime(reader.GetAttribute("start"));
                var stop = ParseTime(reader.GetAttribute("stop"));

                if (channel is null || start is null || stop is null
                    || !offeredChannels.Contains(channel) || stop <= from || start >= to)
                {
                    await reader.SkipAsync().ConfigureAwait(false);
                    more = !reader.EOF;

                    continue;
                }

                programs.Add(await ReadProgramAsync(reader, guideId(channel), start.Value, stop.Value)
                    .ConfigureAwait(false));
            }

            more = await reader.ReadAsync().ConfigureAwait(false);
        }

        return programs;
    }

    private static async Task<Programme> ReadProgramAsync(XmlReader reader, string channelId, DateTimeOffset start, DateTimeOffset stop)
    {
        string? title = null;
        string? subtitle = null;
        string? description = null;
        string? episode = null;
        var genres = new List<string>();
        var live = false;

        if (!reader.IsEmptyElement)
        {
            var depth = reader.Depth;
            await reader.ReadAsync().ConfigureAwait(false);

            // Ends on the programme's own end tag. Reading an element's content, or skipping it,
            // moves onto what follows, so only anything else is read past explicitly.
            while (reader.Depth > depth && !reader.EOF)
            {
                if (reader.NodeType != XmlNodeType.Element)
                {
                    await reader.ReadAsync().ConfigureAwait(false);
                    continue;
                }

                switch (reader.Name)
                {
                    case "title" when title is null:
                        title = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        break;
                    case "sub-title" when subtitle is null:
                        subtitle = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        break;
                    case "desc" when description is null:
                        description = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        break;
                    case "category":
                        genres.Add(await reader.ReadElementContentAsStringAsync().ConfigureAwait(false));
                        break;
                    case "episode-num" when reader.GetAttribute("system") == "xmltv_ns":
                        episode = await reader.ReadElementContentAsStringAsync().ConfigureAwait(false);
                        break;
                    case "live":
                        live = true;
                        await reader.SkipAsync().ConfigureAwait(false);
                        break;
                    default:
                        await reader.SkipAsync().ConfigureAwait(false);
                        break;
                }
            }
        }

        var (season, number) = ParseEpisodeNumber(episode);

        return new Programme
        {
            Id = string.Create(CultureInfo.InvariantCulture, $"{channelId}:{start.ToUnixTimeSeconds()}"),
            ChannelId = channelId,
            Title = string.IsNullOrWhiteSpace(title) ? "Unknown" : title.Trim(),
            EpisodeTitle = string.IsNullOrWhiteSpace(subtitle) ? null : subtitle.Trim(),
            Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
            StartsAt = start,
            EndsAt = stop,
            Genres = [.. genres.Where(genre => !string.IsNullOrWhiteSpace(genre)).Select(genre => genre.Trim())],
            SeasonNumber = season,
            EpisodeNumber = number,
            IsLive = live,
        };
    }

    /// <summary>XMLTV timestamps look like <c>20260824203000 +0200</c>.</summary>
    public static DateTimeOffset? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string[] formats = ["yyyyMMddHHmmss zzz", "yyyyMMddHHmmsszzz", "yyyyMMddHHmmss"];

        return DateTimeOffset.TryParseExact(
            value.Trim(),
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>The <c>xmltv_ns</c> system counts from zero and uses <c>season.episode.part</c>.</summary>
    private static (int? Season, int? Episode) ParseEpisodeNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return (null, null);
        }

        var parts = value.Split('.', StringSplitOptions.TrimEntries);

        static int? Parse(string? part) =>
            int.TryParse(part?.Split('/')[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed + 1
                : null;

        return (Parse(parts.ElementAtOrDefault(0)), Parse(parts.ElementAtOrDefault(1)));
    }
}
