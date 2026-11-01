using System.Buffers;
using Microsoft.AspNetCore.Http.Features;
using Nightwave.Core.Configuration;
using Nightwave.Core.Lineup;
using Nightwave.Core.Output;
using Nightwave.Core.Streaming;
using Nightwave.Core.Tuning;

namespace Nightwave.Server;

/// <summary>What players fetch: a playlist and a guide per bouquet, streams and logos.</summary>
internal static partial class OutputEndpoints
{
    public static OutputLinks Links(HttpRequest request, ConfigStore config, Bouquet bouquet)
    {
        return new OutputLinks(new Uri(HdHomeRunEmulator.BaseAddress(request, config) + "/"), bouquet.Token);
    }

    public static void MapOutputEndpoints(this WebApplication app)
    {
        app.MapGet("/output/{id:guid}/playlist.m3u", async (Guid id, HttpContext context, BouquetStore bouquets, LineupService lineup, ConfigStore config) =>
        {
            if (bouquets.Find(id) is not { } bouquet)
            {
                return BouquetMissing(id);
            }

            var channels = await lineup.ResolveAsync(bouquet, context.RequestAborted);
            var text = PlaylistWriter.Write(bouquet, channels, Links(context.Request, config, bouquet), HasLogo(lineup));

            return Results.Text(text, "audio/x-mpegurl; charset=utf-8");
        });

        app.MapGet("/output/{id:guid}/guide.xml", async (Guid id, HttpContext context, BouquetStore bouquets, LineupService lineup, ConfigStore config) =>
        {
            if (bouquets.Find(id) is not { } bouquet)
            {
                return BouquetMissing(id);
            }

            var channels = await lineup.ResolveAsync(bouquet, context.RequestAborted);
            var now = DateTimeOffset.UtcNow;
            var programmes = await lineup.ProgrammesAsync(
                [.. channels.Select(channel => channel.Channel)],
                now - TimeSpan.FromHours(6),
                now + TimeSpan.FromDays(Math.Clamp(config.Current.Guide.DaysAhead, 1, 14)),
                context.RequestAborted);

            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            context.Response.ContentType = "application/xml; charset=utf-8";

            await XmltvWriter.WriteAsync(context.Response.Body, channels, programmes, Links(context.Request, config, bouquet), HasLogo(lineup), context.RequestAborted);

            return Results.Empty;
        });

        app.MapGet("/logo/{key}", (string key, HttpContext context, LineupService lineup, ChannelLogoStore chosen) =>
        {
            if (!ChannelKey.TryDecode(key, out var channel))
            {
                return Results.NotFound();
            }

            var background = string.Equals(context.Request.Query["background"], "light", StringComparison.OrdinalIgnoreCase)
                ? LogoBackground.Light
                : LogoBackground.Dark;

            if (lineup.FindLogo(channel, background) is not { } logo)
            {
                return Results.NotFound();
            }

            // A logo chosen by hand can change under the same address, so it is asked about each time.
            context.Response.Headers.CacheControl = chosen.Find(channel) is null ? "public, max-age=86400" : "no-cache";

            return Results.Bytes(logo.Content, logo.ContentType, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{logo.Tag}\""));
        });

        app.MapGet("/stream/{key}", StreamAsync);
    }

    private static Func<ChannelKey, bool> HasLogo(LineupService lineup) =>
        lineup.ServesLogo;

    private static IResult BouquetMissing(Guid id) => Results.Json(
        new { code = "output.bouquet_missing", message = $"There is no bouquet {id:N}.", remedy = "It was deleted. Copy the address of another bouquet." },
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>Hands a channel over exactly as it arrives, or says plainly why it cannot.</summary>
    private static async Task StreamAsync(string key, HttpContext context, StreamHub hub, ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger("Nightwave.Stream");
        var cancelled = context.RequestAborted;

        if (!ChannelKey.TryDecode(key, out var channel))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;

            return;
        }

        OpenedChannel opened;

        try
        {
            // A player changing channel may open the new one first. Clients without an address
            // cannot be told apart, so they do not replace anything.
            var address = context.Connection.RemoteIpAddress?.ToString();

            opened = await hub.OpenAsync(channel, address ?? "unknown", address is not null, cancelled);
        }
        catch (NightwaveException error)
        {
            LogRefused(logger, channel.ToString(), error.Message);

            // Players show the status, people read the rest: no free tuner is not a server fault.
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { code = error.Code, message = error.Message, remedy = error.Remedy }, cancelled);

            return;
        }

        if (opened.Redirect is { } redirect)
        {
            context.Response.Redirect(redirect.ToString());

            return;
        }

        try
        {
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            context.Response.ContentType = "video/mp2t";
            context.Response.Headers.CacheControl = "no-store";

            var buffer = ArrayPool<byte>.Shared.Rent(188 * 348);

            try
            {
                while (true)
                {
                    var read = await opened.Content!.ReadAsync(buffer, cancelled);

                    if (read == 0)
                    {
                        break;
                    }

                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancelled);
                    opened.Session.CountSent(read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (OperationCanceledException) when (cancelled.IsCancellationRequested)
        {
            // The player left.
        }
        catch (NightwaveException error)
        {
            // The stream is running, so the status is gone; ending it is what is left to say.
            LogRefused(logger, channel.ToString(), error.Message);
            context.Abort();
        }
        finally
        {
            await opened.Close();
        }
    }

    [LoggerMessage(EventId = 400, Level = LogLevel.Warning, Message = "Channel {Channel} could not be delivered: {Reason}")]
    private static partial void LogRefused(ILogger logger, string channel, string reason);
}
