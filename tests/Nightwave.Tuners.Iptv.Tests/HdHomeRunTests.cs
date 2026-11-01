using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Nightwave.Core.Configuration;
using Nightwave.Core.Tuning;
using Nightwave.Tuners.Iptv.HdHomeRun;
using Nightwave.Tuners.Iptv.Net;

namespace Nightwave.Tuners.Iptv.Tests;

/// <summary>The HDHomeRun as a tuner, against a device made of a local HTTP server, and its search packets.</summary>
public sealed class HdHomeRunTests
{
    [Fact]
    public void A_search_is_a_crc_sealed_frame_for_tuners()
    {
        var request = HdHomeRunProtocol.Request();

        Assert.Equal(new byte[] { 0x00, 0x02, 0x00, 0x06, 0x01, 0x04, 0, 0, 0, 1 }, request[..10]);
        Assert.Equal(Crc(request.AsSpan(0, 10)), BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(10)));
    }

    [Fact]
    public void An_answer_reads_back_and_a_damaged_one_does_not()
    {
        var reply = HdHomeRunProtocol.Reply("1040A1B2", 4, "http://192.0.2.9:9985", "http://192.0.2.9:9985/lineup.json");

        var answer = HdHomeRunProtocol.Read(reply);

        Assert.Equal("1040A1B2", answer!.DeviceId);
        Assert.Equal(new Uri("http://192.0.2.9:9985"), answer.BaseUrl);

        reply[6] ^= 1;

        Assert.Null(HdHomeRunProtocol.Read(reply));
    }

    [Fact]
    public void Our_own_device_ids_pass_the_makers_check()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            Assert.True(HdHomeRunProtocol.IsValidDeviceId(HdHomeRunProtocol.NewDeviceId()));
        }

        Assert.False(HdHomeRunProtocol.IsValidDeviceId("12345678"));
    }

    [Fact]
    public void A_tuner_answers_a_search_for_tuners_and_nothing_else()
    {
        var id = HdHomeRunProtocol.NewDeviceId();

        Assert.True(HdHomeRunProtocol.IsSearchFor(HdHomeRunProtocol.Request(), id));
        Assert.False(HdHomeRunProtocol.IsSearchFor(HdHomeRunProtocol.Reply(id, 2, "http://a/", "http://a/l"), id));

        var damaged = HdHomeRunProtocol.Request();

        damaged[5] ^= 1;

        Assert.False(HdHomeRunProtocol.IsSearchFor(damaged, id));
    }

    [Fact]
    public void A_lineup_leaves_out_what_is_copy_protected()
    {
        using var lineup = JsonDocument.Parse(
            """
            [
              { "GuideNumber": "5.1", "GuideName": "Open", "VideoCodec": "MPEG2", "AudioCodec": "AC3", "URL": "http://192.0.2.5:5004/auto/v5.1" },
              { "GuideNumber": "7.1", "GuideName": "Locked", "DRM": 1, "URL": "http://192.0.2.5:5004/auto/v7.1" },
              { "GuideNumber": "90", "GuideName": "Radio", "AudioCodec": "AAC", "URL": "http://192.0.2.5:5004/auto/v90" }
            ]
            """);

        var (channels, locked) = HdHomeRunTunerProvider.ReadLineup(lineup.RootElement);

        Assert.Equal(["5.1", "90"], channels.Select(channel => channel.Number));
        Assert.False(channels[0].Radio);
        Assert.True(channels[1].Radio);
        Assert.Equal(1, locked);
    }

    [Fact]
    public async Task Channels_and_a_stream_come_from_the_device_and_its_tuners_are_the_limit()
    {
        using var device = new FakeDevice();
        var directory = Directory.CreateTempSubdirectory().FullName;

        try
        {
            var config = new ConfigStore(directory);

            await config.UpdateAsync(current => current.Tuners.Add(new TunerConfig
            {
                Id = "hd",
                Kind = TunerKind.HdHomeRun,
                Name = "Mine",
                HdHomeRun = new HdHomeRunTunerConfig { Address = device.Address },
            }));

            using var http = new IptvHttp(config);
            var provider = new HdHomeRunTunerProvider(config, http, new WebStreams(), NullLogger<HdHomeRunTunerProvider>.Instance);

            var channel = Assert.Single(await provider.GetChannelsAsync());

            Assert.Equal("hd:5.1", channel.Id);
            Assert.Equal("News", channel.Name);
            Assert.Equal(1, Assert.Single(await provider.GetTunersAsync()).MaxStreams);

            var tuned = await provider.OpenAsync(new TunedStreamRequest { ChannelId = channel.Id, ClientDeviceId = "test" });

            await using (tuned)
            {
                var stream = await tuned.OpenReadAsync();

                await using (stream)
                {
                    var buffer = new byte[4];

                    await stream.ReadExactlyAsync(buffer);

                    Assert.Equal("TEST"u8.ToArray(), buffer);
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>The CRC-32 of zlib, worked out the long way, as the maker's library does.</summary>
    private static uint Crc(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;

        foreach (var value in data)
        {
            crc ^= value;

            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (0u - (crc & 1)));
            }
        }

        return ~crc;
    }

    private sealed class FakeDevice : IDisposable
    {
        private readonly HttpListener _listener = new();

        public FakeDevice()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);

            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            Address = $"127.0.0.1:{port}";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string Address { get; }

        public void Dispose() => _listener.Close();

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    var context = await _listener.GetContextAsync();

                    var body = context.Request.Url!.AbsolutePath switch
                    {
                        "/discover.json" => $$"""{"FriendlyName":"Test","TunerCount":1,"BaseURL":"http://{{Address}}","LineupURL":"http://{{Address}}/lineup.json"}""",
                        "/lineup.json" => $$"""[{"GuideNumber":"5.1","GuideName":"News","URL":"http://{{Address}}/auto/v5.1"}]""",
                        _ => "TEST",
                    };

                    var bytes = Encoding.UTF8.GetBytes(body);

                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException)
            {
                // The test is over.
            }
        }
    }
}
