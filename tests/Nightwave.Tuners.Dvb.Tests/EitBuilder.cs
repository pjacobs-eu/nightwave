using System.Text;
using Nightwave.Tuners.Dvb.Mpeg.Si;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>EIT sections and their descriptors, byte by byte after EN 300 468, 5.2.4 and 6.2.</summary>
internal static class EitBuilder
{
    public static byte[] Section(
        int tableId,
        int serviceId,
        int transportStreamId,
        int originalNetworkId,
        int version,
        params (int EventId, DateTimeOffset Start, TimeSpan Duration, byte[] Descriptors)[] events)
    {
        var body = new List<byte>
        {
            (byte)(transportStreamId >> 8), (byte)transportStreamId,
            (byte)(originalNetworkId >> 8), (byte)originalNetworkId,
            0, (byte)tableId,
        };

        foreach (var (eventId, start, duration, descriptors) in events)
        {
            body.Add((byte)(eventId >> 8));
            body.Add((byte)eventId);
            body.AddRange(DvbTime.Write(start));
            body.AddRange(Bcd(duration));
            body.Add((byte)((4 << 5) | (descriptors.Length >> 8)));
            body.Add((byte)descriptors.Length);
            body.AddRange(descriptors);
        }

        return TsBuilder.Section(tableId, serviceId, version, [.. body]);
    }

    public static byte[] ShortEvent(string language, string name, string text) =>
        ShortEvent(language, Encoding.ASCII.GetBytes(name), Encoding.ASCII.GetBytes(text));

    public static byte[] ShortEvent(string language, byte[] name, byte[] text) =>
        TsBuilder.Descriptor(0x4D, [.. Encoding.ASCII.GetBytes(language), (byte)name.Length, .. name, (byte)text.Length, .. text]);

    public static byte[] ExtendedEvent(int number, int last, string language, (string Description, string Item)[] items, byte[] text)
    {
        var itemBytes = items.SelectMany(item =>
        {
            var description = Encoding.ASCII.GetBytes(item.Description);
            var value = Encoding.ASCII.GetBytes(item.Item);

            return (byte[])[(byte)description.Length, .. description, (byte)value.Length, .. value];
        }).ToArray();

        return TsBuilder.Descriptor(
            0x4E,
            [(byte)((number << 4) | last), .. Encoding.ASCII.GetBytes(language), (byte)itemBytes.Length, .. itemBytes, (byte)text.Length, .. text]);
    }

    public static byte[] Content(params int[] nibbles) =>
        TsBuilder.Descriptor(0x54, [.. nibbles.SelectMany(nibble => new[] { (byte)nibble, (byte)0 })]);

    public static byte[] ParentalRating(string country, int rating) =>
        TsBuilder.Descriptor(0x55, [.. Encoding.ASCII.GetBytes(country), (byte)rating]);

    private static byte[] Bcd(TimeSpan duration)
    {
        static byte Digits(int value) => (byte)(((value / 10) << 4) | (value % 10));

        return [Digits((int)duration.TotalHours), Digits(duration.Minutes), Digits(duration.Seconds)];
    }
}

/// <summary>A clock a test sets by hand.</summary>
internal sealed class ManualTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
