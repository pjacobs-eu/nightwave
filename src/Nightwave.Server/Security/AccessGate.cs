using Nightwave.Core.Configuration;
using Nightwave.Core.Lineup;

namespace Nightwave.Server.Security;

/// <summary>
/// Who gets in. The interface holds provider passwords and starts tuners, so everything needs a
/// session, and before a password exists everything leads to setting one. Playlists, guides and
/// streams are fetched by players that cannot log in, so they carry the token of their bouquet.
/// </summary>
internal sealed class AccessGate(RequestDelegate next, ConfigStore config, BouquetStore bouquets)
{
    public static bool IsOutput(PathString path) =>
        path.StartsWithSegments("/output") || path.StartsWithSegments("/stream") || path.StartsWithSegments("/logo");

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;

        if (IsOutput(path))
        {
            // A session opens everything: the interface shows logos of channels in no bouquet.
            if (context.User.Identity?.IsAuthenticated != true && !Opens(path, context.Request.Query["token"].ToString()))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new
                {
                    code = "output.token_required",
                    message = "This address needs the token of its bouquet.",
                    remedy = "Copy the playlist's address again from the bouquet page of Nightwave.",
                });

                return;
            }

            await next(context);

            return;
        }

        if (HdHomeRunEmulator.Answers(path, config))
        {
            await next(context);

            return;
        }

        if (path.StartsWithSegments("/auth") || path.StartsWithSegments("/app.css") || path.StartsWithSegments("/logo.svg") || path.StartsWithSegments("/logo-dark.svg") || path.StartsWithSegments("/_content/Radzen.Blazor/fonts"))
        {
            await next(context);

            return;
        }

        if (config.Current.Access.PasswordHash is null)
        {
            context.Response.Redirect("/auth/setup");

            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.Redirect("/auth/login");

            return;
        }

        await next(context);
    }

    /// <summary>Whether a token is that of the bouquet the address belongs to.</summary>
    private bool Opens(PathString path, string? token)
    {
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];

        if (segments.Length < 2)
        {
            return false;
        }

        return segments[0] == "output"
            ? Guid.TryParse(segments[1], out var bouquet) && bouquets.Opens(bouquet, token)
            : ChannelKey.TryDecode(segments[1], out var channel) && bouquets.Opens(channel, token);
    }
}
