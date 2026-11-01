using System.Globalization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Localization;
using Radzen;
using Nightwave.Core.Configuration;
using Nightwave.Core.Lineup;
using Nightwave.Core.Scanning;
using Nightwave.Core.Storage;
using Nightwave.Core.Streaming;
using Nightwave.Server;
using Nightwave.Server.Components;
using Nightwave.Server.Security;
using Nightwave.Tuners.Iptv;
using Nightwave.Tuners.Dvb;

// The stylesheets and scripts sit next to the program, wherever it is started from: a service
// manager starts it in /, and a content root there answers them empty.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});

// A build, unlike a published copy, finds them through its manifest. Published, this does nothing.
builder.WebHost.UseStaticWebAssets();

var dataDirectory = builder.Configuration["Data"]
    ?? Environment.GetEnvironmentVariable("NIGHTWAVE_DATA")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "nightwave");

Directory.CreateDirectory(dataDirectory);

builder.Services.AddSingleton(new ConfigStore(dataDirectory));
builder.Services.AddSingleton(new NightwaveDatabase(dataDirectory));
builder.Services.AddSingleton<BouquetStore>();
builder.Services.AddSingleton<ChannelLogoStore>();
builder.Services.AddSingleton<LineupService>();
builder.Services.AddSingleton<ScanCoordinator>();
builder.Services.AddSingleton<StreamHub>();
builder.Services.AddSingleton<Texts>();
builder.Services.AddDvbTuners(dataDirectory);
builder.Services.AddIptvTuners();
builder.Services.AddHostedService<BackgroundWorkHost>();
builder.Services.AddHostedService<TelemetrySampler>();
builder.Services.AddHostedService<HdHomeRunResponder>();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "nightwave";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.LoginPath = "/auth/login";
    });

builder.Services.AddRadzenComponents();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

var app = builder.Build();

var localization = new RequestLocalizationOptions
{
    DefaultRequestCulture = new RequestCulture("en"),
    SupportedCultures = [.. Texts.Languages.Select(language => new CultureInfo(language))],
    SupportedUICultures = [.. Texts.Languages.Select(language => new CultureInfo(language))],
};

// The language chosen under Settings comes first; without one, the browser's.
localization.RequestCultureProviders.Insert(0, new CustomRequestCultureProvider(context =>
{
    var language = context.RequestServices.GetRequiredService<ConfigStore>().Current.Language;

    return Task.FromResult(language is not null && Texts.Languages.Contains(language) ? new ProviderCultureResult(language) : null);
}));

app.UseRequestLocalization(localization);

app.UseAuthentication();
app.UseMiddleware<AccessGate>();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapAuthEndpoints();
app.MapOutputEndpoints();
app.MapHdHomeRunEmulator();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

await app.RunAsync();
