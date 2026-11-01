using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Nightwave.Core.Configuration;

namespace Nightwave.Server.Security;

/// <summary>
/// Setting the password and logging in. Plain forms rather than components: a cookie can only be
/// set on an ordinary response, and these pages have to work before anything else may be seen.
/// </summary>
internal static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/auth/setup", (ConfigStore config, Texts t) =>
            config.Current.Access.PasswordHash is null
                ? Page(t["auth.setup.title"], t["auth.setup.intro"], "/auth/setup", t["auth.password"], t["auth.setup.submit"], null, t)
                : Results.Redirect("/auth/login"));

        app.MapPost("/auth/setup", async (HttpContext context, ConfigStore config, Texts t) =>
        {
            var password = (await context.Request.ReadFormAsync())["password"].ToString();

            if (config.Current.Access.PasswordHash is not null)
            {
                return Results.Redirect("/auth/login");
            }

            if (password.Length < AdminPassword.MinimumLength)
            {
                return Page(t["auth.setup.title"], t["auth.setup.intro"], "/auth/setup", t["auth.password"], t["auth.setup.submit"], t["auth.tooShort", AdminPassword.MinimumLength], t);
            }

            await config.UpdateAsync(current => current.Access.PasswordHash = AdminPassword.Hash(password));
            await SignInAsync(context);

            return Results.Redirect("/");
        }).DisableAntiforgery();

        app.MapGet("/auth/login", (ConfigStore config, Texts t) =>
            config.Current.Access.PasswordHash is null
                ? Results.Redirect("/auth/setup")
                : Page(t["auth.login.title"], null, "/auth/login", t["auth.password"], t["auth.login.submit"], null, t));

        app.MapPost("/auth/login", async (HttpContext context, ConfigStore config, Texts t) =>
        {
            var password = (await context.Request.ReadFormAsync())["password"].ToString();

            if (config.Current.Access.PasswordHash is { } stored && AdminPassword.Verify(password, stored))
            {
                await SignInAsync(context);

                return Results.Redirect("/");
            }

            // Hashing already costs time; this makes guessing over the network cost more of it.
            await Task.Delay(TimeSpan.FromSeconds(1));

            return Page(t["auth.login.title"], null, "/auth/login", t["auth.password"], t["auth.login.submit"], t["auth.wrongPassword"], t);
        }).DisableAntiforgery();

        app.MapPost("/auth/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            return Results.Redirect("/auth/login");
        }).DisableAntiforgery();
    }

    private static Task SignInAsync(HttpContext context) => context.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme)),
        new AuthenticationProperties { IsPersistent = true });

    private static IResult Page(string title, string? intro, string action, string label, string submit, string? error, Texts t) => Results.Content(
        $$"""
        <!doctype html>
        <html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{WebUtility.HtmlEncode(title)}} · Nightwave</title><meta name="color-scheme" content="light dark"><link rel="stylesheet" href="/app.css"></head>
        <body class="auth"><form method="post" action="{{action}}" class="auth-card">
        <div class="nw-wordmark" role="img" aria-label="Nightwave"></div><h1>{{WebUtility.HtmlEncode(title)}}</h1>
        {{(intro is null ? string.Empty : $"<p>{WebUtility.HtmlEncode(intro)}</p>")}}
        {{(error is null ? string.Empty : $"<p class=\"auth-error\" role=\"alert\">{WebUtility.HtmlEncode(error)}</p>")}}
        <label>{{WebUtility.HtmlEncode(label)}}<input type="password" name="password" autofocus required autocomplete="current-password"></label>
        <button type="submit">{{WebUtility.HtmlEncode(submit)}}</button></form></body></html>
        """,
        "text/html; charset=utf-8");
}
