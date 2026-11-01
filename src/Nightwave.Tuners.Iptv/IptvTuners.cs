using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nightwave.Core.Tuning;
using Nightwave.Tuners.Iptv.Guide;
using Nightwave.Tuners.Iptv.HdHomeRun;
using Nightwave.Tuners.Iptv.M3u;
using Nightwave.Tuners.Iptv.Net;

namespace Nightwave.Tuners.Iptv;

/// <summary>Tuners reached over HTTP: M3U playlists, each with its own proxy or none, and HDHomeRun devices.</summary>
public static class IptvTuners
{
    public static IServiceCollection AddIptvTuners(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IptvHttp>();
        services.AddSingleton<WebStreams>();
        services.AddSingleton<WebGuideStore>();

        services.AddSingleton<M3uTunerProvider>();
        services.AddSingleton<ITunerProvider>(provider => provider.GetRequiredService<M3uTunerProvider>());

        services.AddSingleton<HdHomeRunTunerProvider>();
        services.AddSingleton<ITunerProvider>(provider => provider.GetRequiredService<HdHomeRunTunerProvider>());
        services.AddSingleton<IChannelScanner, HdHomeRunScanner>();

        AddGuide<M3uTunerProvider>(services);

        return services;
    }

    private static void AddGuide<TSource>(IServiceCollection services)
        where TSource : class, IWebGuideSource
    {
        services.AddSingleton(provider => new KeyedGuide<TSource>(new WebGuideProvider(
            provider.GetRequiredService<TSource>(),
            provider.GetRequiredService<WebGuideStore>(),
            provider.GetRequiredService<ILogger<WebGuideProvider>>())));
        services.AddSingleton<IGuideProvider>(provider => provider.GetRequiredService<KeyedGuide<TSource>>().Guide);
        services.AddSingleton<IBackgroundWork>(provider => provider.GetRequiredService<KeyedGuide<TSource>>().Guide);
    }

    /// <summary>The one guide of a kind of tuner, registered under two contracts.</summary>
    private sealed record KeyedGuide<TSource>(WebGuideProvider Guide);
}
