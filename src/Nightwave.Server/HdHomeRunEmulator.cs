using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Nightwave.Core.Configuration;
using Nightwave.Core.Lineup;
using Nightwave.Core.Output;
using Nightwave.Core.Tuning;
using Nightwave.Tuners.Iptv.HdHomeRun;

namespace Nightwave.Server;

/// <summary>
/// One bouquet shown to the outside as a HDHomeRun, for media servers that take live TV from
/// nothing else: they find it on the network or by its address, read its lineup, and play the
/// streams from there. Streams go out as they arrive; whoever cannot play them has to transcode.
/// </summary>
/// <remarks>
/// A media server cannot be given a token, so whoever reaches this port gets the bouquet's lineup
/// and with it the addresses of its streams. That is why it is off until a bouquet is chosen, and
/// why it is one bouquet and not all of them.
/// </remarks>
internal static class HdHomeRunEmulator
{
    private const string ModelNumber = "HDTC-2US";
    private const string FirmwareName = "hdhomeruntc_atsc";
    private const string FirmwareVersion = "20240101";
    private static readonly string[] Sources = ["Cable"];

    /// <summary>Whether a request is for the emulated device, which takes no login and no token.</summary>
    public static bool Answers(PathString path, ConfigStore config) =>
        config.Current.Output.HdHomeRun.Bouquet is not null
        && (path == "/discover.json" || path == "/lineup.json" || path == "/lineup_status.json" || path == "/lineup.post");

    /// <summary>The address media servers reach this server at, from the settings or the request.</summary>
    public static string BaseAddress(HttpRequest request, ConfigStore config)
    {
        var output = config.Current.Output;

        return string.IsNullOrWhiteSpace(output.PublicUrl) ? $"{request.Scheme}://{request.Host}" : output.PublicUrl.TrimEnd('/');
    }

    public static void MapHdHomeRunEmulator(this WebApplication app)
    {
        app.MapGet("/discover.json", (HttpContext context, ConfigStore config, BouquetStore bouquets) =>
        {
            if (Chosen(config, bouquets) is not { } bouquet)
            {
                return Results.NotFound();
            }

            var settings = config.Current.Output.HdHomeRun;
            var address = BaseAddress(context.Request, config);

            return Results.Json(new Dictionary<string, object?>
            {
                ["FriendlyName"] = $"Nightwave {bouquet.Name}",
                ["ModelNumber"] = ModelNumber,
                ["FirmwareName"] = FirmwareName,
                ["FirmwareVersion"] = FirmwareVersion,
                ["DeviceID"] = settings.DeviceId,
                ["DeviceAuth"] = "nightwave",
                ["TunerCount"] = settings.Tuners,
                ["BaseURL"] = address,
                ["LineupURL"] = $"{address}/lineup.json",
            });
        });

        app.MapGet("/lineup.json", async (HttpContext context, ConfigStore config, BouquetStore bouquets, LineupService lineup) =>
        {
            if (Chosen(config, bouquets) is not { } bouquet)
            {
                return Results.NotFound();
            }

            var links = OutputEndpoints.Links(context.Request, config, bouquet);
            var channels = await lineup.ResolveAsync(bouquet, context.RequestAborted);

            return Results.Json(channels
                .Where(channel => channel.Channel.Kind == ChannelKind.Television)
                .Select(channel => new Dictionary<string, string>
                {
                    ["GuideNumber"] = channel.Number.ToString(CultureInfo.InvariantCulture),
                    ["GuideName"] = channel.Channel.Name,
                    ["URL"] = links.Stream(channel.Key).ToString(),
                }));
        });

        app.MapGet("/lineup_status.json", () => Results.Json(new Dictionary<string, object>
        {
            ["ScanInProgress"] = 0,
            ["ScanPossible"] = 1,
            ["Source"] = "Cable",
            ["SourceList"] = Sources,
        }));

        // Media servers ask for a scan before they read the lineup. Nothing is ever scanned here.
        app.MapPost("/lineup.post", () => Results.Ok());
    }

    private static Bouquet? Chosen(ConfigStore config, BouquetStore bouquets) =>
        config.Current.Output.HdHomeRun.Bouquet is { } id ? bouquets.Find(id) : null;
}

/// <summary>Answers the search media servers send over the network, while a bouquet is chosen.</summary>
internal sealed partial class HdHomeRunResponder(ConfigStore config, IServer server, ILogger<HdHomeRunResponder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        UdpClient socket;

        try
        {
            socket = new UdpClient(new IPEndPoint(IPAddress.Any, HdHomeRunProtocol.Port));
        }
        catch (SocketException error)
        {
            LogPortTaken(logger, HdHomeRunProtocol.Port, error.Message);

            return;
        }

        using (socket)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var received = await socket.ReceiveAsync(stoppingToken);
                    var settings = config.Current.Output.HdHomeRun;

                    if (settings.Bouquet is not null
                        && settings.DeviceId is { } id
                        && HdHomeRunProtocol.IsSearchFor(received.Buffer, id)
                        && BaseAddress(received.RemoteEndPoint) is { } address)
                    {
                        await socket.SendAsync(HdHomeRunProtocol.Reply(id, settings.Tuners, address, $"{address}/lineup.json"), received.RemoteEndPoint, stoppingToken);
                    }
                }
                catch (SocketException error)
                {
                    LogAnswerFailed(logger, error.Message);
                }
            }
        }
    }

    /// <summary>The address to give the one who asked: the one of the settings, or the one of the way back to them.</summary>
    private string? BaseAddress(IPEndPoint asker)
    {
        if (config.Current.Output.PublicUrl is { Length: > 0 } address)
        {
            return address.TrimEnd('/');
        }

        var listening = server.Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        var port = listening?[(listening.LastIndexOf(':') + 1)..].TrimEnd('/');

        using var probe = new Socket(asker.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

        probe.Connect(asker);

        return port is null || probe.LocalEndPoint is not IPEndPoint local ? null : $"http://{local.Address}:{port}";
    }

    [LoggerMessage(EventId = 400, Level = LogLevel.Warning, Message = "UDP port {Port} is taken, so media servers will not find the emulated HDHomeRun by themselves: {Reason}. Enter this server's address in them instead.")]
    private static partial void LogPortTaken(ILogger logger, int port, string reason);

    [LoggerMessage(EventId = 401, Level = LogLevel.Information, Message = "A search for the emulated HDHomeRun could not be answered: {Reason}")]
    private static partial void LogAnswerFailed(ILogger logger, string reason);
}
