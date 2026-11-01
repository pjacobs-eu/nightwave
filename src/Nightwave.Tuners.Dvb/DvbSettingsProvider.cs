using Nightwave.Core.Configuration;

namespace Nightwave.Tuners.Dvb;

/// <summary>
/// What the SAT>IP backend works with, taken from the configuration: every enabled tuner of
/// kind SAT>IP is a device, and descrambling and guide settings apply to all of them.
/// </summary>
public sealed class DvbSettingsProvider
{
    private readonly Func<DvbSettings> _read;
    private DvbSettings? _cached;

    public DvbSettingsProvider(ConfigStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _read = () => From(store.Current);
        store.Changed += (_, _) =>
        {
            _cached = null;
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>Fixed settings, for tests.</summary>
    public DvbSettingsProvider(DvbSettings settings) => _read = () => settings;

    /// <summary>Raised after the settings were replaced. On the thread that saved; handlers do no I/O.</summary>
    public event EventHandler? Changed;

    /// <summary>The settings as they stand.</summary>
    public DvbSettings Current => _cached ??= _read();

    public ValueTask<DvbSettings> GetAsync(CancellationToken cancellationToken = default) => new(Current);

    internal static DvbSettings From(NightwaveConfig config) => new()
    {
        AutoDiscover = config.DiscoverSatIp,
        Devices =
        [
            .. config.Tuners
                .Where(tuner => tuner is { Kind: TunerKind.SatIp, Enabled: true })
                .Select(tuner => new SatIpDeviceSettings
                {
                    Address = tuner.SatIp.Address,
                    DescriptionUrl = tuner.SatIp.DescriptionUrl,
                    Name = tuner.Name,
                    MaxTuners = tuner.SatIp.MaxTuners,
                }),
        ],
        DvbCards =
        [
            .. config.Tuners
                .Where(tuner => tuner is { Kind: TunerKind.Dvb, Enabled: true })
                .Select(tuner => new DvbCardSettings
                {
                    Adapter = tuner.Dvb.Adapter,
                    Frontend = tuner.Dvb.Frontend,
                    Name = tuner.Name,
                    Dish = new LinuxDvb.DishSettings
                    {
                        LowKhz = tuner.Dvb.LnbLowMhz * 1000,
                        HighKhz = tuner.Dvb.LnbHighMhz * 1000,
                        SwitchKhz = tuner.Dvb.LnbSwitchMhz * 1000,
                        Switch = tuner.Dvb.Switch,
                        Unicable = tuner.Dvb.Unicable,
                        UnicableSlot = tuner.Dvb.UnicableSlot,
                        UnicableKhz = tuner.Dvb.UnicableMhz * 1000,
                        UnicablePin = tuner.Dvb.UnicablePin,
                        Rotor = tuner.Dvb.Rotor,
                        RotorPositions = LinuxDvb.DishSettings.ParsePositions(tuner.Dvb.RotorPositions),
                        SiteLatitude = tuner.Dvb.SiteLatitude,
                        SiteLongitude = tuner.Dvb.SiteLongitude,
                        RotorMsPerDegree = tuner.Dvb.RotorMsPerDegree,
                    },
                }),
        ],
        DescramblingEnabled = config.Descrambling.Enabled,
        DvbApiHost = config.Descrambling.Host,
        DvbApiPort = config.Descrambling.Port,
        EpgEnabled = config.Guide.BroadcastEnabled,
        EpgIntervalHours = config.Guide.IntervalHours,
        EpgMaxTuners = config.Guide.MaxTuners,
        EpgLanguages = config.Guide.Languages,
    };
}
