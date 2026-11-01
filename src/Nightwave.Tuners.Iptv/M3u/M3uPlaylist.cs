using System.Text.RegularExpressions;

namespace Nightwave.Tuners.Iptv.M3u;

/// <summary>One playlist entry.</summary>
public sealed record M3uEntry(string Id, string Name, string? Number, string? GuideId, Uri? Logo, Uri StreamUrl, bool IsRadio = false);

/// <summary>Reads the extended M3U format.</summary>
public static partial class M3uPlaylist
{
    /// <summary>
    /// An <c>#EXTINF</c> line with attributes and a display name, followed by the stream URL.
    /// Lines that do not follow that shape are skipped.
    /// </summary>
    public static IReadOnlyList<M3uEntry> Parse(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var entries = new List<M3uEntry>();
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // The address of this entry is the next line that is not a directive, and only up to
            // the next #EXTINF: searching further would give an entry whose address is missing
            // the address of a later one, and the viewer would watch a channel they did not pick.
            string? url = null;

            for (var next = i + 1; next < lines.Length; next++)
            {
                if (lines[next].StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                if (!lines[next].StartsWith('#'))
                {
                    url = lines[next];
                    break;
                }
            }

            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var streamUri))
            {
                continue;
            }

            var attributes = AttributePattern().Matches(lines[i])
                .GroupBy(match => match.Groups["key"].Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Groups["value"].Value, StringComparer.OrdinalIgnoreCase);

            var commaIndex = lines[i].LastIndexOf(',');
            var name = commaIndex >= 0 ? lines[i][(commaIndex + 1)..].Trim() : url;

            var id = attributes.GetValueOrDefault("tvg-id");

            if (string.IsNullOrWhiteSpace(id))
            {
                id = name;
            }

            entries.Add(new M3uEntry(
                id,
                name,
                attributes.GetValueOrDefault("tvg-chno"),
                attributes.GetValueOrDefault("tvg-id"),
                Uri.TryCreate(attributes.GetValueOrDefault("tvg-logo"), UriKind.Absolute, out var logo) ? logo : null,
                streamUri,
                string.Equals(attributes.GetValueOrDefault("radio"), "true", StringComparison.OrdinalIgnoreCase)));
        }

        return entries;
    }

    [GeneratedRegex(@"(?<key>[\w-]+)=""(?<value>[^""]*)""", RegexOptions.ExplicitCapture)]
    private static partial Regex AttributePattern();
}
