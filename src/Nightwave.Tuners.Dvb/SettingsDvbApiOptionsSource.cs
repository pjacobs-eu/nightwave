using Nightwave.Tuners.Dvb.Descrambling;
using Nightwave.Tuners.Dvb.Devices;

namespace Nightwave.Tuners.Dvb;

/// <summary>
/// Where the descrambling server is, as the Descrambling section of the settings says. Nothing
/// when descrambling is switched off or no address is set - which is what makes scrambled
/// channels fail with an error saying so, rather than with one about a connection that was
/// never going to work.
/// </summary>
internal sealed class SettingsDvbApiOptionsSource : IDvbApiOptionsSource
{
    private readonly DvbSettingsProvider _settings;

    public SettingsDvbApiOptionsSource(DvbSettingsProvider settings)
    {
        _settings = settings;
    }

    public async Task<DvbApiOptions?> GetAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);

        return From(settings);
    }

    /// <summary>
    /// The options for these settings. A port typed into the address wins over the port field,
    /// since that is the one somebody looked at last.
    /// </summary>
    internal static DvbApiOptions? From(DvbSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.DescramblingEnabled || string.IsNullOrWhiteSpace(settings.DvbApiHost))
        {
            return null;
        }

        var (host, port) = DeviceDirectory.SplitAddress(settings.DvbApiHost);

        return new DvbApiOptions
        {
            Host = host,
            Port = port ?? (settings.DvbApiPort is > 0 and <= 65535 ? settings.DvbApiPort : 9000),
        };
    }
}
