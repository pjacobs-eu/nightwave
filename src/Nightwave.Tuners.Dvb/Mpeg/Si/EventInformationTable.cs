using System.Text;
using Nightwave.Tuners.Dvb.Mpeg.Psi;

namespace Nightwave.Tuners.Dvb.Mpeg.Si;

/// <summary>
/// An EIT section (EN 300 468, 5.2.4): the programmes of one service. Present/following
/// (0x4E, 0x4F) says what is on now and next; schedule (0x50 to 0x6F) the days ahead. The
/// "other" tables carry the programmes of services on other transport streams, which is what
/// lets one transponder fill the guide of a whole satellite.
/// </summary>
/// <remarks>
/// Only the standard EIT is read. The proprietary guides some pay operators send instead -
/// OpenTV (Sky UK, Sky Italia) and MediaHighway (Canal+) - use their own pids and formats and
/// are not decoded; their channels simply have no guide from the broadcast. Sky Deutschland and
/// HD+ send standard EIT.
/// </remarks>
internal sealed record EventInformationTable
{
    public const int Pid = 0x12;

    public const int PresentFollowingActual = 0x4E;
    public const int PresentFollowingOther = 0x4F;

    public required int TableId { get; init; }

    public required int ServiceId { get; init; }

    public required int TransportStreamId { get; init; }

    public required int OriginalNetworkId { get; init; }

    public required int Version { get; init; }

    public required int SectionNumber { get; init; }

    public required int LastSectionNumber { get; init; }

    public required IReadOnlyList<EitEvent> Events { get; init; }

    public bool IsSchedule => TableId is >= 0x50 and <= 0x6F;

    /// <summary>About this transport stream, rather than another one of the network.</summary>
    public bool IsActual => TableId is PresentFollowingActual or >= 0x50 and <= 0x5F;

    public static bool IsEitTable(int tableId) => tableId is >= 0x4E and <= 0x6F;

    public static EventInformationTable? Parse(ReadOnlySpan<byte> section)
    {
        if (!SectionHeader.TryRead(section, out var header) || !IsEitTable(header.TableId) || !header.IsCurrent)
        {
            return null;
        }

        var body = SectionHeader.Body(section);
        if (body.Length < 6)
        {
            return null;
        }

        var events = new List<EitEvent>();
        var position = 6;

        while (position + 12 <= body.Length)
        {
            var eventId = (body[position] << 8) | body[position + 1];
            var start = DvbTime.Read(body.Slice(position + 2, 5));
            var duration = DvbTime.ReadDuration(body.Slice(position + 7, 3));
            var running = (body[position + 10] >> 5) & 0x07;
            var scrambled = (body[position + 10] & 0x10) != 0;
            var loopLength = ((body[position + 10] & 0x0F) << 8) | body[position + 11];

            if (position + 12 + loopLength > body.Length)
            {
                break;
            }

            var descriptors = Descriptor.ReadLoop(body.Slice(position + 12, loopLength));
            events.Add(EitEvent.Create(eventId, start, duration, running, scrambled, descriptors));
            position += 12 + loopLength;
        }

        return new EventInformationTable
        {
            TableId = header.TableId,
            ServiceId = header.TableIdExtension,
            TransportStreamId = (body[0] << 8) | body[1],
            OriginalNetworkId = (body[2] << 8) | body[3],
            Version = header.Version,
            SectionNumber = header.SectionNumber,
            LastSectionNumber = header.LastSectionNumber,
            Events = events,
        };
    }
}

/// <summary>One event of an EIT, with its descriptors read.</summary>
internal sealed record EitEvent
{
    public const int ShortEventTag = 0x4D;
    public const int ExtendedEventTag = 0x4E;
    public const int ContentTag = 0x54;
    public const int ParentalRatingTag = 0x55;

    public required int EventId { get; init; }

    /// <summary>Null where the broadcaster left the start undefined (all ones), as it may for a following event.</summary>
    public DateTimeOffset? Start { get; init; }

    public TimeSpan? Duration { get; init; }

    /// <summary>1 not running, 2 starts in a few seconds, 3 pausing, 4 running (EN 300 468, table 6).</summary>
    public int RunningStatus { get; init; }

    public bool IsScrambled { get; init; }

    /// <summary>Title and short text, one per language the broadcaster sent.</summary>
    public IReadOnlyList<ShortEvent> ShortEvents { get; init; } = [];

    /// <summary>The long description, one per language, its parts already put together.</summary>
    public IReadOnlyList<ExtendedEvent> ExtendedEvents { get; init; } = [];

    /// <summary>The content nibbles: level 1 in the upper half, level 2 in the lower.</summary>
    public IReadOnlyList<int> Content { get; init; } = [];

    /// <summary>Country code to minimum age, where the broadcaster rates the event.</summary>
    public IReadOnlyDictionary<string, int> ParentalRatings { get; init; } = new Dictionary<string, int>();

    public static EitEvent Create(int eventId, DateTimeOffset? start, TimeSpan? duration, int running, bool scrambled, IReadOnlyList<Descriptor> descriptors)
    {
        var shortEvents = new List<ShortEvent>();
        var extendedParts = new List<(int Number, string Language, List<(string Description, string Item)> Items, byte[] Text)>();
        var content = new List<int>();
        var ratings = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var descriptor in descriptors)
        {
            var data = descriptor.Data;

            switch (descriptor.Tag)
            {
                case ShortEventTag when data.Length >= 5:
                {
                    var language = Language(data);
                    var nameLength = data[3];
                    if (4 + nameLength + 1 > data.Length)
                    {
                        break;
                    }

                    var name = DvbText.Decode(data.AsSpan(4, nameLength));
                    var textLength = data[4 + nameLength];
                    var text = 5 + nameLength + textLength <= data.Length
                        ? DvbText.Decode(data.AsSpan(5 + nameLength, textLength))
                        : string.Empty;

                    shortEvents.Add(new ShortEvent(language, name, text.Length == 0 ? null : text));
                    break;
                }

                case ExtendedEventTag when data.Length >= 6:
                {
                    var number = data[0] >> 4;
                    var language = Language(data.AsSpan(1));
                    var itemsLength = data[4];
                    var items = new List<(string, string)>();
                    var position = 5;
                    var itemsEnd = Math.Min(data.Length, 5 + itemsLength);

                    while (position + 2 <= itemsEnd)
                    {
                        var descriptionLength = data[position];
                        if (position + 1 + descriptionLength + 1 > itemsEnd)
                        {
                            break;
                        }

                        var description = DvbText.Decode(data.AsSpan(position + 1, descriptionLength));
                        var itemLength = data[position + 1 + descriptionLength];
                        var itemStart = position + 2 + descriptionLength;
                        if (itemStart + itemLength > itemsEnd)
                        {
                            break;
                        }

                        items.Add((description, DvbText.Decode(data.AsSpan(itemStart, itemLength))));
                        position = itemStart + itemLength;
                    }

                    var textAt = 5 + itemsLength;
                    var text = textAt < data.Length && textAt + 1 + data[textAt] <= data.Length
                        ? data.AsSpan(textAt + 1, data[textAt]).ToArray()
                        : [];

                    extendedParts.Add((number, language, items, text));
                    break;
                }

                case ContentTag:
                    for (var position = 0; position + 2 <= data.Length; position += 2)
                    {
                        content.Add(data[position]);
                    }

                    break;

                case ParentalRatingTag:
                    for (var position = 0; position + 4 <= data.Length; position += 4)
                    {
                        var rating = data[position + 3];

                        // 0x01 to 0x0F: the minimum age is the rating plus three.
                        if (rating is >= 0x01 and <= 0x0F)
                        {
                            ratings[Encoding.ASCII.GetString(data, position, 3).ToUpperInvariant()] = rating + 3;
                        }
                    }

                    break;
            }
        }

        return new EitEvent
        {
            EventId = eventId,
            Start = start,
            Duration = duration,
            RunningStatus = running,
            IsScrambled = scrambled,
            ShortEvents = shortEvents,
            ExtendedEvents = ExtendedEvent.Join(extendedParts),
            Content = content,
            ParentalRatings = ratings,
        };
    }

    private static string Language(ReadOnlySpan<byte> data) =>
        Encoding.ASCII.GetString(data[..3]).ToLowerInvariant();
}

/// <summary>A short event descriptor: the title and a line under it, in one language.</summary>
internal sealed record ShortEvent(string Language, string Name, string? Text);

/// <summary>An extended event description in one language, put together from all its parts.</summary>
internal sealed record ExtendedEvent(string Language, IReadOnlyList<(string Description, string Item)> Items, string Text)
{
    /// <summary>
    /// Puts the parts of each language together in the order of their descriptor numbers. The
    /// text bytes are joined before they are decoded - a broadcaster cuts a description wherever
    /// its 255 bytes end, which in UTF-8 can be the middle of a character - and only the first
    /// part's character table selector is kept.
    /// </summary>
    public static IReadOnlyList<ExtendedEvent> Join(
        IEnumerable<(int Number, string Language, List<(string Description, string Item)> Items, byte[] Text)> parts) =>
        [
            .. parts
                .GroupBy(part => part.Language, StringComparer.Ordinal)
                .Select(group =>
                {
                    var ordered = group.OrderBy(part => part.Number).ToList();
                    var bytes = new List<byte>();

                    for (var index = 0; index < ordered.Count; index++)
                    {
                        var text = ordered[index].Text.AsSpan();

                        if (index > 0 && bytes.Count > 0 && bytes[0] < 0x20 && text.Length > 0 && text[0] < 0x20)
                        {
                            text = text[SelectorLength(text)..];
                        }

                        bytes.AddRange(text.ToArray());
                    }

                    return new ExtendedEvent(
                        group.Key,
                        [.. ordered.SelectMany(part => part.Items)],
                        DvbText.Decode(bytes.ToArray()));
                }),
        ];

    /// <summary>How many bytes the character table selector at the start of a text takes (EN 300 468, Annex A.2).</summary>
    private static int SelectorLength(ReadOnlySpan<byte> text) => text[0] switch
    {
        0x10 => Math.Min(3, text.Length),
        0x1F => Math.Min(2, text.Length),
        _ => 1,
    };
}

/// <summary>The time fields of DVB SI (EN 300 468, Annex C): a Modified Julian Date and BCD hours, minutes, seconds, in UTC.</summary>
internal static class DvbTime
{
    /// <summary>Day zero of the Modified Julian Date.</summary>
    private static readonly DateTime MjdEpoch = new(1858, 11, 17, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Reads a start time: two bytes MJD, three bytes BCD. All ones is "undefined" and null.</summary>
    public static DateTimeOffset? Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 5 || data[..5].IndexOfAnyExcept((byte)0xFF) < 0)
        {
            return null;
        }

        var mjd = (data[0] << 8) | data[1];
        var time = ReadDuration(data[2..]);

        return time is null ? null : new DateTimeOffset(MjdEpoch.AddDays(mjd) + time.Value);
    }

    /// <summary>Reads three BCD bytes of hours, minutes and seconds.</summary>
    public static TimeSpan? ReadDuration(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3)
        {
            return null;
        }

        static int Bcd(byte value) => ((value >> 4) * 10) + (value & 0x0F);

        if ((data[0] & 0x0F) > 9 || (data[1] & 0x0F) > 9 || (data[2] & 0x0F) > 9 || data[1] >> 4 > 5 || data[2] >> 4 > 5)
        {
            return null;
        }

        return new TimeSpan(Bcd(data[0]), Bcd(data[1]), Bcd(data[2]));
    }

    /// <summary>Writes a start time the way EN 300 468 does, for tests and for nothing else.</summary>
    public static byte[] Write(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        var mjd = (int)(utc.Date - MjdEpoch).TotalDays;

        static byte Bcd(int number) => (byte)(((number / 10) << 4) | (number % 10));

        return [(byte)(mjd >> 8), (byte)mjd, Bcd(utc.Hour), Bcd(utc.Minute), Bcd(utc.Second)];
    }
}
