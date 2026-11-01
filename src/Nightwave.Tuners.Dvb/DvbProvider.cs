using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nightwave.Core.Configuration;
using Nightwave.Core.Storage;
using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb.Channels;
using Nightwave.Tuners.Dvb.Descrambling;
using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.Epg;
using Nightwave.Tuners.Dvb.Scanning;
using Nightwave.Tuners.Dvb.Streaming;
using Nightwave.Tuners.Dvb.Transport;

namespace Nightwave.Tuners.Dvb;

/// <summary>
/// The SAT>IP backend: television from SAT>IP servers on the local network, channels found by
/// scanning, and scrambled channels opened through a descrambling server.
/// </summary>
public static class DvbProvider
{
    public const string Id = "satip";

    /// <summary>The client for device descriptions: short requests on the local network.</summary>
    public const string DeviceClientName = Id + ".devices";

    public static IServiceCollection AddDvbTuners(this IServiceCollection services, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Devices on the local network answer quickly or not at all.
        services.AddHttpClient(DeviceClientName, client => client.Timeout = TimeSpan.FromSeconds(10));

        var directory = Path.Combine(dataDirectory, "satip");

        services.AddSingleton(provider => new DvbSettingsProvider(provider.GetRequiredService<ConfigStore>()));
        services.AddSingleton<IDeviceDiscovery, NetworkDeviceDiscovery>();
        services.AddSingleton(provider => new InputCache(directory, provider.GetRequiredService<ILogger<InputCache>>()));
        services.AddSingleton(provider => new DeviceDirectory(
            provider.GetRequiredService<DvbSettingsProvider>(),
            provider.GetRequiredService<IDeviceDiscovery>(),
            TimeProvider.System,
            provider.GetRequiredService<InputCache>()));

        services.AddSingleton(provider => new ChannelStore(provider.GetRequiredService<NightwaveDatabase>()));
        services.AddSingleton<ITsTransportFactory, TsTransportFactory>();
        services.AddSingleton<DescramblerSlots>();
        services.AddSingleton(_ => new FeedTimings());
        services.AddSingleton(_ => new ScanTimings());

        services.AddSingleton<IDvbApiOptionsSource, SettingsDvbApiOptionsSource>();
        services.AddSingleton<IServiceDescramblerFactory, DvbApiDescramblerFactory>();

        services.AddSingleton(provider => new EpgStore(provider.GetRequiredService<NightwaveDatabase>(), TimeProvider.System));
        services.AddSingleton<EpgService>();

        services.AddSingleton(provider => new TunerPool(
            provider.GetRequiredService<ITsTransportFactory>(),
            provider.GetRequiredService<IServiceDescramblerFactory>(),
            provider.GetRequiredService<DescramblerSlots>(),
            provider.GetRequiredService<FeedTimings>(),
            provider.GetRequiredService<ILoggerFactory>(),
            provider.GetRequiredService<EpgService>()));

        services.AddSingleton(_ => new EpgGrabberTimings());
        services.AddSingleton<EpgGrabber>();
        services.AddSingleton<IBackgroundWork>(provider => provider.GetRequiredService<EpgGrabber>());
        services.AddSingleton<IGuideProvider, DvbGuideProvider>();

        services.AddSingleton(provider => PiconCatalogue.FromFile(
            Path.Combine(AppContext.BaseDirectory, "picons.zip"), Path.Combine(dataDirectory, "picons.zip")));

        services.AddSingleton<ITunerProvider>(provider => new DvbTunerProvider(
            provider.GetRequiredService<ChannelStore>(),
            provider.GetRequiredService<DeviceDirectory>(),
            provider.GetRequiredService<TunerPool>(),
            provider.GetRequiredService<IServiceDescramblerFactory>(),
            provider.GetRequiredService<ILogger<DvbTunerProvider>>(),
            provider.GetRequiredService<PiconCatalogue>()));

        services.AddSingleton(provider => new InputDetector(
            provider.GetRequiredService<InputCache>(),
            provider.GetRequiredService<TunerPool>(),
            provider.GetRequiredService<ITsTransportFactory>(),
            provider.GetRequiredService<ScanTimings>(),
            TimeProvider.System,
            provider.GetRequiredService<ILogger<InputDetector>>()));

        services.AddSingleton<IChannelScanner>(provider => new DvbChannelScanner(
            provider.GetRequiredService<DeviceDirectory>(),
            provider.GetRequiredService<ChannelStore>(),
            provider.GetRequiredService<TunerPool>(),
            provider.GetRequiredService<ITsTransportFactory>(),
            provider.GetRequiredService<InputDetector>(),
            provider.GetRequiredService<ScanTimings>(),
            TimeProvider.System,
            provider.GetRequiredService<ILogger<DvbChannelScanner>>()));

        return services;
    }
}
