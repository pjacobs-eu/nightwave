using System.Globalization;
using Nightwave.Tuners.Dvb.Mpeg.Si;

namespace Nightwave.Tuners.Dvb.Epg;

/// <summary>
/// A DVB service as the guide knows it: original network, transport stream, service. The same
/// three numbers a channel is stored under, so a channel and its programmes find each other
/// whichever device received either.
/// </summary>
internal readonly record struct DvbServiceKey(int OriginalNetworkId, int TransportStreamId, int ServiceId)
{
    private const string Prefix = "dvb:";

    /// <summary>
    /// What a channel's <c>GuideId</c> is and what the guide's programmes carry as their
    /// channel: <c>dvb:1.1019.28006</c>. An operator who prefers an XMLTV guide maps a channel
    /// onto it with the server's override instead.
    /// </summary>
    public string GuideId => string.Create(CultureInfo.InvariantCulture, $"{Prefix}{OriginalNetworkId}.{TransportStreamId}.{ServiceId}");

    public static bool TryParse(string? guideId, out DvbServiceKey key)
    {
        key = default;

        if (guideId is null || !guideId.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = guideId[Prefix.Length..].Split('.');

        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var onid)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var tsid)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var sid))
        {
            return false;
        }

        key = new DvbServiceKey(onid, tsid, sid);

        return true;
    }
}

/// <summary>One programme as the guide keeps it, in the language that was picked for it.</summary>
internal sealed record EpgEvent
{
    public required int EventId { get; init; }

    public required DateTimeOffset Start { get; init; }

    public required DateTimeOffset End { get; init; }

    public required string Title { get; init; }

    /// <summary>The short event's text where it is short enough to be a subtitle or an episode title.</summary>
    public string? Subtitle { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>The lowest minimum age any country's rating names.</summary>
    public int? MinimumAge { get; init; }

    public bool IsLive { get; init; }

    /// <summary>The ISO 639-2 code of the language the text is in.</summary>
    public string? Language { get; init; }

    /// <summary>
    /// The guide's version of an EIT event: the texts in the preferred language, the genres,
    /// the rating. Null for an event without a start, a duration or a title - a placeholder the
    /// broadcaster fills in later.
    /// </summary>
    public static EpgEvent? From(EitEvent source, IReadOnlyList<string> languages)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(languages);

        if (source.Start is not { } start || source.Duration is not { } duration || duration <= TimeSpan.Zero)
        {
            return null;
        }

        var shortEvent = EpgLanguages.Pick(source.ShortEvents, shortEvent => shortEvent.Language, languages);
        if (shortEvent is null || string.IsNullOrWhiteSpace(shortEvent.Name))
        {
            return null;
        }

        var extended = EpgLanguages.Pick(source.ExtendedEvents, extendedEvent => extendedEvent.Language, languages, shortEvent.Language);
        var extendedText = extended is null ? null : Describe(extended);

        // The short text is a subtitle on most channels and a short summary on some. Short, it is
        // taken for the one; long, or with no other description, for the other.
        var shortText = shortEvent.Text;
        var isSubtitle = shortText is { Length: <= 80 } && !string.Equals(shortText, shortEvent.Name, StringComparison.Ordinal);

        return new EpgEvent
        {
            EventId = source.EventId,
            Start = start,
            End = start + duration,
            Title = shortEvent.Name,
            Subtitle = isSubtitle && extendedText is not null ? shortText : null,
            Description = extendedText ?? shortText,
            Genres = ContentGenres.From(source.Content),
            MinimumAge = source.ParentalRatings.Count == 0 ? null : source.ParentalRatings.Values.Min(),
            IsLive = source.Content.Contains(ContentGenres.Live),
            Language = shortEvent.Language,
        };
    }

    private static string? Describe(ExtendedEvent extended)
    {
        var lines = extended.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.Item))
            .Select(item => string.IsNullOrWhiteSpace(item.Description) ? item.Item : $"{item.Description}: {item.Item}")
            .ToList();

        if (!string.IsNullOrWhiteSpace(extended.Text))
        {
            lines.Insert(0, extended.Text);
        }

        return lines.Count == 0 ? null : string.Join('\n', lines);
    }
}

/// <summary>
/// Which of the languages a broadcaster sends a programme in to show. EIT language codes are
/// ISO 639-2, where German is both <c>deu</c> and <c>ger</c> depending on who wrote the stream.
/// </summary>
internal static class EpgLanguages
{
    /// <summary>ISO 639-2 terminology codes and their bibliographic twins.</summary>
    private static readonly Dictionary<string, string> Bibliographic = new(StringComparer.Ordinal)
    {
        ["deu"] = "ger",
        ["fra"] = "fre",
        ["zho"] = "chi",
        ["nld"] = "dut",
        ["ces"] = "cze",
        ["ell"] = "gre",
        ["fas"] = "per",
        ["ron"] = "rum",
        ["slk"] = "slo",
        ["sqi"] = "alb",
        ["mkd"] = "mac",
        ["msa"] = "may",
        ["isl"] = "ice",
        ["eus"] = "baq",
        ["cym"] = "wel",
        ["hye"] = "arm",
        ["kat"] = "geo",
        ["mya"] = "bur",
        ["bod"] = "tib",
    };

    /// <summary>
    /// The languages to prefer: those written in the settings, or else the server's own language
    /// in both spellings followed by English.
    /// </summary>
    public static IReadOnlyList<string> Preferred(string? configured, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);

        var codes = new List<string>();

        void Add(string code)
        {
            var lower = code.Trim().ToLowerInvariant();
            if (lower.Length != 3 || codes.Contains(lower))
            {
                return;
            }

            codes.Add(lower);

            foreach (var (terminology, bibliographic) in Bibliographic)
            {
                if (lower == terminology && !codes.Contains(bibliographic))
                {
                    codes.Add(bibliographic);
                }
                else if (lower == bibliographic && !codes.Contains(terminology))
                {
                    codes.Add(terminology);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            foreach (var code in configured.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                Add(code);
            }

            if (codes.Count > 0)
            {
                return codes;
            }
        }

        Add(culture.ThreeLetterISOLanguageName);
        Add("eng");

        return codes;
    }

    /// <summary>The first item in a preferred language; else one in <paramref name="fallback"/>; else the first there is.</summary>
    public static T? Pick<T>(IReadOnlyList<T> items, Func<T, string> language, IReadOnlyList<string> preferred, string? fallback = null)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(items);

        foreach (var code in preferred)
        {
            if (items.FirstOrDefault(item => string.Equals(language(item), code, StringComparison.OrdinalIgnoreCase)) is { } match)
            {
                return match;
            }
        }

        if (fallback is not null
            && items.FirstOrDefault(item => string.Equals(language(item), fallback, StringComparison.OrdinalIgnoreCase)) is { } same)
        {
            return same;
        }

        return items.Count > 0 ? items[0] : null;
    }
}
