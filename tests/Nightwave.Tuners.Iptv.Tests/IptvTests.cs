using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Nightwave.Core.Configuration;
using Nightwave.Core.Tuning;
using Nightwave.Tuners.Iptv.Guide;
using Nightwave.Tuners.Iptv.M3u;
using Nightwave.Tuners.Iptv.Net;

namespace Nightwave.Tuners.Iptv.Tests;

public sealed class IptvTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("nightwave-iptv-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void A_playlist_reads_its_attributes_and_an_entry_without_address_takes_none_from_the_next()
    {
        var entries = M3uPlaylist.Parse("""
            #EXTM3U
            #EXTINF:-1 tvg-id="ard.de" tvg-chno="1" tvg-logo="http://x/a.png",Das Erste
            http://x/1.ts
            #EXTINF:-1,No address
            #EXTINF:-1 radio="true",Radio
            http://x/2.ts
            """);

        Assert.Equal(["Das Erste", "Radio"], entries.Select(entry => entry.Name));
        Assert.Equal("ard.de", entries[0].GuideId);
        Assert.Equal("1", entries[0].Number);
        Assert.True(entries[1].IsRadio);
    }

    [Fact]
    public void A_proxy_that_cannot_be_used_stops_the_request_instead_of_going_direct()
    {
        using var config = new ConfigStore(_directory);
        using var http = new IptvHttp(config);
        var tuner = new TunerConfig { Kind = TunerKind.M3u, Name = "A", Proxy = new ProxyConfig { Enabled = true, Url = "not a proxy" } };

        var error = Assert.Throws<TunerException>(() => http.Stream(tuner));

        Assert.Equal("proxy.invalid", error.Code);
    }

    [Fact]
    public void A_proxy_setting_on_a_satellite_tuner_means_nothing()
    {
        using var config = new ConfigStore(_directory);
        using var http = new IptvHttp(config);
        var tuner = new TunerConfig { Kind = TunerKind.SatIp, Proxy = new ProxyConfig { Enabled = true, Url = "not a proxy" } };

        Assert.Null(IptvHttp.AddressOf(tuner));
        Assert.NotNull(http.Api(tuner));
    }

    [Fact]
    public void Credentials_come_from_their_fields_or_out_of_the_address_and_never_stay_in_it()
    {
        var tuner = new TunerConfig { Kind = TunerKind.M3u, Proxy = new ProxyConfig { Enabled = true, Url = "socks5://me:s%40cret@proxy:1080" } };

        Assert.Equal("socks5://proxy:1080/", IptvHttp.AddressOf(tuner)!.ToString());
        Assert.Equal("s@cret", IptvHttp.CredentialsOf(tuner)!.Password);

        tuner.Proxy.Username = "other";
        tuner.Proxy.Password = "field";

        Assert.Equal("field", IptvHttp.CredentialsOf(tuner)!.Password);
    }

    [Fact]
    public async Task Two_viewers_of_a_channel_share_one_connection()
    {
        var streams = new WebStreams();
        var connections = 0;

        Task<Stream> Open(CancellationToken _)
        {
            connections++;

            return Task.FromResult<Stream>(new MemoryStream(new byte[188]));
        }

        await using var first = streams.TryOpen("t", "t:1", 1, Open);
        await using var second = streams.TryOpen("t", "t:1", 1, Open);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(1, streams.ActiveOn("t"));

        await (await first.OpenReadAsync()).DisposeAsync();
        await (await second.OpenReadAsync()).DisposeAsync();

        Assert.Equal(1, connections);
    }

    [Fact]
    public async Task Past_the_limit_there_is_no_queue_and_a_place_comes_back_with_the_last_viewer()
    {
        var streams = new WebStreams();

        static Task<Stream> Open(CancellationToken _) => Task.FromResult<Stream>(new MemoryStream());

        var held = streams.TryOpen("t", "t:1", 1, Open)!;

        Assert.Null(streams.TryOpen("t", "t:2", 1, Open));

        await held.DisposeAsync();

        Assert.Equal(0, streams.ActiveOn("t"));

        await using var next = streams.TryOpen("t", "t:2", 1, Open);

        Assert.NotNull(next);
    }

    [Fact]
    public async Task At_the_limit_a_client_takes_over_the_place_of_their_stream()
    {
        var streams = new WebStreams();
        var reason = new InvalidOperationException("replaced");

        static Task<Stream> Hold(CancellationToken token) => Task.FromResult<Stream>(new BlockingStream(token));

        var old = streams.TryOpen("t", "t:1", 1, Hold, new StreamClaim("tv", reason))!;
        var reading = await old.OpenReadAsync();
        var next = streams.TryOpen("t", "t:2", 1, Hold, new StreamClaim("tv", reason));

        Assert.NotNull(next);
        Assert.Equal(1, streams.ActiveOn("t"));
        Assert.Same(reason, await Assert.ThrowsAsync<InvalidOperationException>(async () => await reading.ReadExactlyAsync(new byte[188])));

        // The old player leaving does not give back the place the new stream is on.
        await old.DisposeAsync();
        Assert.Equal(1, streams.ActiveOn("t"));

        await (await next.OpenReadAsync()).DisposeAsync();
        await next.DisposeAsync();
        Assert.Equal(0, streams.ActiveOn("t"));
    }

    [Fact]
    public async Task At_the_limit_nobody_elses_stream_is_taken()
    {
        var streams = new WebStreams();
        var reason = new InvalidOperationException("replaced");

        static Task<Stream> Open(CancellationToken _) => Task.FromResult<Stream>(new MemoryStream());

        await using var held = streams.TryOpen("t", "t:1", 1, Open, new StreamClaim("tv", reason));

        Assert.Null(streams.TryOpen("t", "t:2", 1, Open, new StreamClaim("radio", reason)));
        Assert.Null(streams.TryOpen("t", "t:2", 1, Open));
    }

    [Fact]
    public async Task A_refused_stream_reaches_whoever_asked()
    {
        var streams = new WebStreams();

        await using var stream = streams.TryOpen(
            "t",
            "t:1",
            null,
            _ => throw new TunerException("m3u", "tuner.stream_refused", "refused", "fix it"))!;

        var error = await Assert.ThrowsAsync<TunerException>(() => stream.OpenReadAsync());

        Assert.Equal("tuner.stream_refused", error.Code);
    }

    [Fact]
    public async Task A_feed_keeps_what_is_offered_and_near()
    {
        var now = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

        static string Time(DateTimeOffset at) => at.ToString("yyyyMMddHHmmss +0000", CultureInfo.InvariantCulture);

        var xml = $"""
            <tv>
              <programme channel="a" start="{Time(now)}" stop="{Time(now.AddHours(1))}"><title>Wanted</title><episode-num system="xmltv_ns">1.2.</episode-num></programme>
              <programme channel="b" start="{Time(now)}" stop="{Time(now.AddHours(1))}"><title>Other channel</title></programme>
              <programme channel="a" start="{Time(now.AddDays(30))}" stop="{Time(now.AddDays(31))}"><title>Too far</title></programme>
            </tv>
            """;

        using var feed = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var programmes = await XmltvReader.ReadAsync(feed, id => $"t:{id}", new HashSet<string> { "a" }, now, CancellationToken.None);

        var programme = Assert.Single(programmes);

        Assert.Equal("Wanted", programme.Title);
        Assert.Equal("t:a", programme.ChannelId);
        Assert.Equal((2, 3), (programme.SeasonNumber, programme.EpisodeNumber));
    }

    [Fact]
    public async Task A_web_playlist_of_segments_is_handed_over_or_refused_with_a_proxy_but_never_dropped_silently()
    {
        var path = Path.Combine(_directory, "list.m3u");

        await File.WriteAllTextAsync(path, "#EXTM3U\n#EXTINF:-1 tvg-id=\"a\",A\nhttp://x/a.m3u8\n");

        using var config = new ConfigStore(_directory);

        await config.UpdateAsync(current => current.Tuners.Add(new TunerConfig { Id = "t", Kind = TunerKind.M3u, Name = "Web", M3u = new M3uTunerConfig { PlaylistUrl = path, MaxStreams = 1 } }));

        using var http = new IptvHttp(config);
        var provider = new M3uTunerProvider(config, http, new WebStreams(), NullLogger<M3uTunerProvider>.Instance);

        await using var direct = await provider.OpenAsync(new TunedStreamRequest { ChannelId = "t:a", ClientDeviceId = "test" });

        Assert.Equal("http://x/a.m3u8", direct.DirectAddress!.ToString());

        await config.UpdateAsync(current => current.Tuners[0].Proxy = new ProxyConfig { Enabled = true, Url = "http://proxy:1" });

        var error = await Assert.ThrowsAsync<TunerException>(() => provider.OpenAsync(new TunedStreamRequest { ChannelId = "t:a", ClientDeviceId = "test" }));

        Assert.Equal("tuner.proxy_needs_byte_stream", error.Code);
    }

    [Fact]
    public async Task A_stream_comes_through_a_local_provider_and_a_refusal_says_so()
    {
        using var listener = new HttpListener();

        var port = System.Net.Sockets.TcpListener.Create(0);

        port.Start();

        var free = ((IPEndPoint)port.LocalEndpoint).Port;

        port.Stop();
        listener.Prefixes.Add($"http://127.0.0.1:{free}/");
        listener.Start();

        var serving = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                var context = await listener.GetContextAsync();

                context.Response.StatusCode = context.Request.Url!.AbsolutePath == "/ok.ts" ? 200 : 403;

                if (context.Response.StatusCode == 200)
                {
                    await context.Response.OutputStream.WriteAsync(new byte[1880]);
                }

                context.Response.Close();
            }
        });

        var path = Path.Combine(_directory, "list.m3u");

        await File.WriteAllTextAsync(path, $"#EXTM3U\n#EXTINF:-1 tvg-id=\"ok\",Ok\nhttp://127.0.0.1:{free}/ok.ts\n#EXTINF:-1 tvg-id=\"no\",No\nhttp://127.0.0.1:{free}/no.ts\n");

        using var config = new ConfigStore(_directory);

        await config.UpdateAsync(current => current.Tuners.Add(new TunerConfig { Id = "t", Kind = TunerKind.M3u, Name = "Web", M3u = new M3uTunerConfig { PlaylistUrl = path, MaxStreams = 2 } }));

        using var http = new IptvHttp(config);
        var provider = new M3uTunerProvider(config, http, new WebStreams(), NullLogger<M3uTunerProvider>.Instance);

        await using (var tuned = await provider.OpenAsync(new TunedStreamRequest { ChannelId = "t:ok", ClientDeviceId = "test" }))
        {
            await using var content = await tuned.OpenReadAsync();
            var buffer = new byte[188];

            Assert.Equal(188, await content.ReadAtLeastAsync(buffer, 188));
        }

        await using (var refused = await provider.OpenAsync(new TunedStreamRequest { ChannelId = "t:no", ClientDeviceId = "test" }))
        {
            var error = await Assert.ThrowsAsync<TunerException>(() => refused.OpenReadAsync());

            Assert.Equal("tuner.stream_refused", error.Code);
        }

        listener.Stop();
    }

    /// <summary>A connection that stays open and silent until it is closed.</summary>
    private sealed class BlockingStream(CancellationToken stop) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, CancellationTokenSource.CreateLinkedTokenSource(stop, cancellationToken).Token);

            return 0;
        }
    }
}
