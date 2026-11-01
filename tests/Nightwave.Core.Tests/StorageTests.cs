using Nightwave.Core.Configuration;
using Nightwave.Core.Lineup;
using Nightwave.Core.Storage;

namespace Nightwave.Core.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("nightwave-storage-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Only_reception_over_the_air_is_repaired()
    {
        Assert.True(new TunerConfig { Kind = TunerKind.SatIp }.SupportsRepair);
        Assert.False(new TunerConfig { Kind = TunerKind.M3u, Repair = true }.SupportsRepair);
    }

    [Fact]
    public async Task A_token_opens_its_own_bouquet_and_the_channels_in_it()
    {
        using var store = new BouquetStore(new NightwaveDatabase(_directory));

        var sport = await store.CreateAsync("Sport");
        var news = await store.CreateAsync("News");
        var football = new ChannelKey("satip", "football");
        var weather = new ChannelKey("satip", "weather");

        await store.SetChannelsAsync(sport.Id, [football]);
        await store.SetChannelsAsync(news.Id, [weather]);

        Assert.NotEqual(sport.Token, news.Token);
        Assert.True(store.Opens(sport.Id, sport.Token));
        Assert.False(store.Opens(sport.Id, news.Token));
        Assert.False(store.Opens(sport.Id, null));
        Assert.True(store.Opens(football, sport.Token));
        Assert.False(store.Opens(football, news.Token));
        Assert.False(store.Opens(new ChannelKey("satip", "elsewhere"), sport.Token));
    }

    [Fact]
    public async Task A_new_token_shuts_the_old_one_out_and_survives_a_restart()
    {
        var database = new NightwaveDatabase(_directory);
        Guid id;
        string fresh;

        using (var store = new BouquetStore(database))
        {
            var bouquet = await store.CreateAsync("Sport");
            var channel = new ChannelKey("satip", "a");

            id = bouquet.Id;
            await store.SetChannelsAsync(id, [channel]);
            await store.NewTokenAsync(id);
            fresh = store.Find(id)!.Token;

            Assert.NotEqual(bouquet.Token, fresh);
            Assert.False(store.Opens(id, bouquet.Token));
            Assert.False(store.Opens(channel, bouquet.Token));
        }

        using var again = new BouquetStore(new NightwaveDatabase(_directory));

        Assert.True(again.Opens(id, fresh));
    }

    [Fact]
    public async Task A_channel_taken_out_of_a_bouquet_no_longer_opens_with_its_token()
    {
        using var store = new BouquetStore(new NightwaveDatabase(_directory));

        var bouquet = await store.CreateAsync("Sport");
        var channel = new ChannelKey("satip", "a");

        await store.SetChannelsAsync(bouquet.Id, [channel]);
        await store.SetChannelsAsync(bouquet.Id, []);

        Assert.False(store.Opens(channel, bouquet.Token));
    }

    [Fact]
    public async Task A_bouquet_from_before_tokens_gets_one_that_stays()
    {
        var database = new NightwaveDatabase(_directory);
        Guid id;

        using (var store = new BouquetStore(database))
        {
            id = (await store.CreateAsync("Old")).Id;
        }

        using (var connection = database.Open())
        {
            NightwaveDatabase.Execute(connection, "UPDATE bouquets SET token = '';");
        }

        string given;

        using (var store = new BouquetStore(new NightwaveDatabase(_directory)))
        {
            given = store.Find(id)!.Token;

            Assert.NotEmpty(given);
        }

        using var again = new BouquetStore(new NightwaveDatabase(_directory));

        Assert.Equal(given, again.Find(id)!.Token);
    }

    [Fact]
    public async Task Bouquets_survive_a_restart_in_their_order()
    {
        Guid news;

        using (var store = new BouquetStore(new NightwaveDatabase(_directory)))
        {
            var first = await store.CreateAsync("Sport");
            var second = await store.CreateAsync("News");

            news = second.Id;
            await store.SetChannelsAsync(news, [new ChannelKey("satip", "b"), new ChannelKey("m3u", "a")]);
            await store.ReorderAsync([second.Id, first.Id]);
        }

        using var again = new BouquetStore(new NightwaveDatabase(_directory));

        Assert.Equal(["News", "Sport"], again.All.Select(bouquet => bouquet.Name));
        Assert.Equal([new ChannelKey("satip", "b"), new ChannelKey("m3u", "a")], again.Find(news)!.Channels);
    }

    [Fact]
    public async Task A_channel_is_in_a_bouquet_once_whatever_its_case()
    {
        using var store = new BouquetStore(new NightwaveDatabase(_directory));
        var bouquet = await store.CreateAsync("One");

        await store.SetChannelsAsync(bouquet.Id, [new ChannelKey("satip", "A"), new ChannelKey("SATIP", "a")]);

        Assert.Single(store.Find(bouquet.Id)!.Channels);
    }

    [Fact]
    public async Task A_name_has_to_fit()
    {
        using var store = new BouquetStore(new NightwaveDatabase(_directory));

        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync("   "));
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateAsync(new string('x', BouquetStore.MaxNameLength + 1)));
    }

    [Fact]
    public async Task Clearing_the_cache_keeps_the_bouquets_and_a_reset_does_not()
    {
        var database = new NightwaveDatabase(_directory);

        using var store = new BouquetStore(database);

        await store.CreateAsync("Kept");

        database.ClearCache();
        Assert.Single(store.All);

        database.Reset();
        Assert.Empty(store.All);

        using var again = new BouquetStore(new NightwaveDatabase(_directory));
        Assert.Empty(again.All);
    }

    [Fact]
    public async Task The_configuration_is_kept_private_and_a_damaged_file_stops_the_start()
    {
        using (var config = new ConfigStore(_directory))
        {
            await config.UpdateAsync(current => current.Tuners.Add(new TunerConfig { Kind = TunerKind.M3u, Name = "A" }));
        }

        var path = Path.Combine(_directory, "config.json");

        using (var again = new ConfigStore(_directory))
        {
            Assert.Equal("A", Assert.Single(again.Current.Tuners).Name);
        }

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }

        await File.WriteAllTextAsync(path, "{ not json");

        Assert.Throws<InvalidDataException>(() => new ConfigStore(_directory));
    }

    [Fact]
    public async Task Only_web_tuners_have_a_proxy()
    {
        var proxy = new ProxyConfig { Enabled = true, Url = "http://proxy:1" };

        Assert.Null(new TunerConfig { Kind = TunerKind.SatIp, Proxy = proxy }.EffectiveProxy);
        Assert.NotNull(new TunerConfig { Kind = TunerKind.M3u, Proxy = proxy }.EffectiveProxy);
        Assert.Null(new TunerConfig { Kind = TunerKind.M3u, Proxy = new ProxyConfig { Url = "http://proxy:1" } }.EffectiveProxy);

        await Task.CompletedTask;
    }
}
