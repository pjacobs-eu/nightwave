using System.Text;
using System.Xml.Linq;
using Nightwave.Core.Lineup;
using Nightwave.Core.Output;
using Nightwave.Core.Tuning;

namespace Nightwave.Core.Tests;

public sealed class OutputTests
{
    private static readonly Bouquet News = new() { Id = Guid.NewGuid(), Name = "News", Position = 0 };

    private static readonly OutputLinks Links = new(new Uri("http://tv.example:9985/"), "secret");

    private static NumberedChannel Channel(int number, string name, string? guide = "guide-1", ChannelKind kind = ChannelKind.Television) =>
        new(number, new TvChannel { Id = $"id{number}", ProviderId = "satip", Name = name, GuideId = guide, Kind = kind });

    [Fact]
    public void A_playlist_lists_its_channels_in_order_through_this_server()
    {
        var text = PlaylistWriter.Write(News, [Channel(1, "Das Erste"), Channel(2, "Radio", kind: ChannelKind.Radio)], Links, _ => false);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("#EXTM3U url-tvg=\"http://tv.example:9985/output/", lines[0], StringComparison.Ordinal);
        Assert.Contains("tvg-chno=\"1\"", lines[1], StringComparison.Ordinal);
        Assert.EndsWith(",Das Erste", lines[1], StringComparison.Ordinal);
        Assert.Contains("group-title=\"News\"", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("http://tv.example:9985/stream/", lines[2], StringComparison.Ordinal);
        Assert.EndsWith("?token=secret", lines[2], StringComparison.Ordinal);
        Assert.Contains("radio=\"true\"", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void A_tuner_s_own_logo_is_served_from_here_and_a_foreign_one_stays_where_it_is()
    {
        var own = PlaylistWriter.Write(News, [Channel(1, "A")], Links, _ => true);
        var foreign = Channel(1, "A") with { Channel = Channel(1, "A").Channel with { Logo = new Uri("http://logos.example/a.png") } };

        Assert.Contains("tvg-logo=\"http://tv.example:9985/logo/", own, StringComparison.Ordinal);
        Assert.Contains("tvg-logo=\"http://logos.example/a.png\"", PlaylistWriter.Write(News, [foreign], Links, _ => false), StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_cannot_break_out_of_its_line()
    {
        var text = PlaylistWriter.Write(News, [Channel(1, "Evil\n#EXTINF:-1,\nhttp://bad/ \"x\"")], Links, _ => false);

        Assert.Equal(3, text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task A_guide_is_valid_xmltv_and_keeps_to_its_channels()
    {
        var programme = new Programme
        {
            Id = "1",
            ChannelId = "guide-1",
            Title = "Tagesschau & mehr",
            Description = "Text\u0001mit Steuerzeichen",
            StartsAt = new DateTimeOffset(2026, 10, 31, 20, 0, 0, TimeSpan.FromHours(1)),
            EndsAt = new DateTimeOffset(2026, 10, 31, 20, 15, 0, TimeSpan.FromHours(1)),
            Genres = ["News"],
            SeasonNumber = 2,
            EpisodeNumber = 5,
        };

        using var output = new MemoryStream();

        await XmltvWriter.WriteAsync(output, [Channel(1, "Das Erste")], new[] { programme }.ToLookup(item => item.ChannelId, StringComparer.OrdinalIgnoreCase), Links, _ => false);

        var document = XDocument.Parse(Encoding.UTF8.GetString(output.ToArray()));
        var written = Assert.Single(document.Root!.Elements("programme"));

        Assert.Equal("guide-1", Assert.Single(document.Root.Elements("channel")).Attribute("id")!.Value);
        Assert.Equal("20261031190000 +0000", written.Attribute("start")!.Value);
        Assert.Equal("Tagesschau & mehr", written.Element("title")!.Value);
        Assert.Equal("Text mit Steuerzeichen", written.Element("desc")!.Value);
        Assert.Equal("1.4.", written.Element("episode-num")!.Value);
    }

    [Fact]
    public void A_channel_key_survives_a_url_segment()
    {
        var key = new ChannelKey("satip", "19.2E:1.1019.10301");

        Assert.True(ChannelKey.TryDecode(key.Encoded, out var decoded));
        Assert.Equal(key, decoded);
        Assert.DoesNotContain('/', key.Encoded);
        Assert.False(ChannelKey.TryDecode("###", out _));
    }
}
