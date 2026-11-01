using Microsoft.Extensions.Logging.Abstractions;
using Nightwave.Core.Lineup;
using Nightwave.Core.Output;
using Nightwave.Core.Storage;
using Nightwave.Core.Tuning;

namespace Nightwave.Core.Tests;

/// <summary>Logos chosen by hand: they go before what a tuner offers, wherever a channel goes out.</summary>
public sealed class ChannelLogoTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly ChannelKey WithPicon = new("sat", "a");
    private static readonly ChannelKey WithAddress = new("web", "b");
    private static readonly OutputLinks Links = new(new Uri("http://tv.example/"), "secret");

    private readonly string _directory = Directory.CreateTempSubdirectory().FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    private static LineupService Lineup(ChannelLogoStore logos) =>
        new([new Provider("sat", picon: true), new Provider("web", picon: false)], [], NullLogger<LineupService>.Instance, logos);

    private static async Task<string> PlaylistAsync(LineupService lineup)
    {
        var bouquet = new Bouquet { Id = Guid.NewGuid(), Name = "All", Position = 0, Channels = [WithPicon, WithAddress] };

        return PlaylistWriter.Write(bouquet, await lineup.ResolveAsync(bouquet), Links, lineup.ServesLogo);
    }

    [Fact]
    public async Task Standard_is_what_the_tuner_offers()
    {
        using var logos = new ChannelLogoStore(new NightwaveDatabase(_directory));
        var text = await PlaylistAsync(Lineup(logos));

        Assert.Contains($"tvg-logo=\"http://tv.example/logo/{WithPicon.Encoded}?token=secret\"", text, StringComparison.Ordinal);
        Assert.Contains("tvg-logo=\"http://logos.example/b.png\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removed_takes_a_picon_and_a_playlist_logo_away()
    {
        using var logos = new ChannelLogoStore(new NightwaveDatabase(_directory));
        var lineup = Lineup(logos);

        await logos.RemoveAsync(WithPicon);
        await logos.RemoveAsync(WithAddress);

        Assert.DoesNotContain("tvg-logo", await PlaylistAsync(lineup), StringComparison.Ordinal);
        Assert.Null(lineup.FindLogo(WithPicon, LogoBackground.Dark));
    }

    [Fact]
    public async Task A_picture_of_our_own_is_served_from_here_even_without_a_picon()
    {
        using var logos = new ChannelLogoStore(new NightwaveDatabase(_directory));
        var lineup = Lineup(logos);

        await logos.SetFileAsync(WithAddress, Png);

        Assert.True(lineup.ServesLogo(WithAddress));
        Assert.Equal("image/png", lineup.FindLogo(WithAddress, LogoBackground.Dark)!.ContentType);
        Assert.Contains($"logo/{WithAddress.Encoded}?token=secret", await PlaylistAsync(lineup), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_address_of_our_own_goes_out_as_it_is()
    {
        using var logos = new ChannelLogoStore(new NightwaveDatabase(_directory));
        var lineup = Lineup(logos);

        await logos.SetAddressAsync(WithPicon, "https://mine.example/logo.png");

        Assert.False(lineup.ServesLogo(WithPicon));
        Assert.Contains("tvg-logo=\"https://mine.example/logo.png\"", await PlaylistAsync(lineup), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Back_to_standard_brings_the_tuners_logo_back()
    {
        using var logos = new ChannelLogoStore(new NightwaveDatabase(_directory));
        var lineup = Lineup(logos);

        await logos.RemoveAsync(WithPicon);
        await logos.ResetAsync(WithPicon);

        Assert.True(lineup.ServesLogo(WithPicon));
        Assert.Null(logos.Find(WithPicon));
    }

    [Fact]
    public async Task Choices_survive_a_restart_and_a_cleared_cache_but_not_a_reset()
    {
        var database = new NightwaveDatabase(_directory);

        using (var logos = new ChannelLogoStore(database))
        {
            await logos.SetFileAsync(WithPicon, Png);
            await logos.SetAddressAsync(WithAddress, "https://mine.example/b.png");
            database.ClearCache();
        }

        var again = new NightwaveDatabase(_directory);

        using var kept = new ChannelLogoStore(again);

        Assert.Equal(Png, kept.Find(WithPicon)!.Picture!.Content.ToArray());
        Assert.Equal(new Uri("https://mine.example/b.png"), kept.Find(WithAddress)!.Address);

        again.Reset();

        Assert.Null(kept.Find(WithPicon));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "image/webp")]
    public void Pictures_are_known_by_their_content(byte[] content, string type) =>
        Assert.Equal(type, ChannelLogoStore.PictureType(content));

    [Fact]
    public async Task Svg_and_other_files_are_refused_whatever_they_are_called()
    {
        using var logos = new ChannelLogoStore(new NightwaveDatabase(_directory));

        await Assert.ThrowsAsync<ArgumentException>(() => logos.SetFileAsync(WithPicon, "<svg onload='alert(1)'/>"u8.ToArray()));
        await Assert.ThrowsAsync<ArgumentException>(() => logos.SetFileAsync(WithPicon, new byte[ChannelLogoStore.MaxBytes + 1]));
        await Assert.ThrowsAsync<ArgumentException>(() => logos.SetAddressAsync(WithPicon, "javascript:alert(1)"));
        await Assert.ThrowsAsync<ArgumentException>(() => logos.SetAddressAsync(WithPicon, "/etc/passwd"));
        Assert.Null(logos.Find(WithPicon));
    }

    private sealed class Provider(string id, bool picon) : ITunerProvider, IChannelLogoSource
    {
        public string ProviderId => id;

        public string DisplayNameKey => id;

        public TunerCapabilities Capabilities => TunerCapabilities.None;

        public Task<IReadOnlyList<TvChannel>> GetChannelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TvChannel>>(
            [
                new TvChannel
                {
                    Id = picon ? "a" : "b",
                    ProviderId = id,
                    Name = "Channel",
                    Logo = picon ? null : new Uri("http://logos.example/b.png"),
                },
            ]);

        public Task<IReadOnlyList<TunerStatus>> GetTunersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ITunedStream> OpenAsync(TunedStreamRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public bool HasLogo(string channelId) => picon;

        public ChannelLogo? FindLogo(string channelId, LogoBackground background) => picon ? new ChannelLogo(new byte[1], "image/png", "tag") : null;
    }
}
