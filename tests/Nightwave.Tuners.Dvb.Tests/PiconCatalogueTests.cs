using System.IO.Compression;
using System.Text;
using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb.Channels;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>
/// Channel logos compiled into the plugin, found by the DVB numbers of a channel the way an
/// Enigma2 receiver names its picons.
/// </summary>
public sealed class PiconCatalogueTests
{
    [Theory]
    // Das Erste HD on Astra 19.2°E: onid 1, tsid 1019, sid 10301.
    [InlineData("19.2E:1.1019.10301", "283D_3FB_1_C00000")]
    [InlineData("13.0E:318.12400.4911", "132F_3070_13E_820000")]
    [InlineData("28.2E:2.2045.6940", "1B1C_7FD_2_11A0000")]
    // West is counted back from 360: 30.0°W is 330.0.
    [InlineData("30.0W:1.2.3", "3_2_1_CE40000")]
    public void A_satellite_channel_is_found_by_its_service_reference(string channelId, string key) =>
        Assert.Equal(key, PiconCatalogue.Key(channelId));

    [Theory]
    [InlineData("C@device-1:1.2.3")]
    [InlineData("T@device-1:1.2.3")]
    [InlineData("19.2E:1.2")]
    [InlineData("not a channel")]
    public void Cable_terrestrial_and_foreign_ids_have_no_picon(string channelId) =>
        Assert.Null(PiconCatalogue.Key(channelId));

    [Fact]
    public void A_dark_page_gets_the_light_logo_and_a_light_page_the_dark_one()
    {
        using var catalogue = new PiconCatalogue(Archive(
            ("index.txt", "283D_3FB_1_C00000=daserste\n"),
            ("light/daserste.png", "light"),
            ("dark/daserste.png", "dark")));

        Assert.True(catalogue.Has("19.2E:1.1019.10301"));
        Assert.Equal("light", Text(catalogue.Find("19.2E:1.1019.10301", LogoBackground.Dark)));
        Assert.Equal("dark", Text(catalogue.Find("19.2E:1.1019.10301", LogoBackground.Light)));
        Assert.NotEqual(
            catalogue.Find("19.2E:1.1019.10301", LogoBackground.Dark)!.Tag,
            catalogue.Find("19.2E:1.1019.10301", LogoBackground.Light)!.Tag);
    }

    [Fact]
    public void A_logo_drawn_in_one_style_only_is_shown_on_either_background()
    {
        using var catalogue = new PiconCatalogue(Archive(
            ("index.txt", "283D_3FB_1_C00000=daserste\n"),
            ("light/daserste.png", "light")));

        Assert.Equal("light", Text(catalogue.Find("19.2E:1.1019.10301", LogoBackground.Light)));
    }

    [Fact]
    public void A_channel_without_a_picon_has_no_logo()
    {
        using var catalogue = new PiconCatalogue(Archive(("index.txt", "283D_3FB_1_C00000=daserste\n")));

        Assert.False(catalogue.Has("19.2E:1.1019.10302"));
        Assert.Null(catalogue.Find("19.2E:1.1019.10302", LogoBackground.Dark));
    }

    [Fact]
    public void A_plugin_built_without_picons_works_without_logos()
    {
        using var catalogue = new PiconCatalogue(null);

        Assert.Equal(0, catalogue.Count);
        Assert.False(catalogue.Has("19.2E:1.1019.10301"));
        Assert.Null(catalogue.Find("19.2E:1.1019.10301", LogoBackground.Dark));
    }

    private static MemoryStream Archive(params (string Name, string Content)[] entries)
    {
        var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static string? Text(ChannelLogo? logo) =>
        logo is null ? null : Encoding.UTF8.GetString(logo.Content.Span).TrimStart('﻿');
}
