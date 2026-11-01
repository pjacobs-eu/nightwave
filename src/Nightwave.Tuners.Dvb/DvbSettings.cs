using System.Globalization;

namespace Nightwave.Tuners.Dvb;

/// <summary>
/// Settings of the SAT>IP backend, as taken from the configuration.
/// </summary>
public sealed class DvbSettings
{
    /// <summary>Whether the network is searched for devices. Listed devices are used either way.</summary>
    public bool AutoDiscover { get; set; } = true;

    public IList<SatIpDeviceSettings> Devices { get; init; } = [];

    /// <summary>The frontends of DVB cards in this machine that are to be used.</summary>
    public IList<DvbCardSettings> DvbCards { get; init; } = [];

    /// <summary>Whether scrambled channels are handed to the descrambling server.</summary>
    public bool DescramblingEnabled { get; set; }

    /// <summary>The machine the descrambling server runs on.</summary>
    public string? DvbApiHost { get; set; }

    /// <summary>The <c>listen_port</c> of the descrambling server's dvbapi section.</summary>
    public int DvbApiPort { get; set; } = 9000;

    /// <summary>Whether the guide is read from the broadcast: while tuners stream, and by the grabber.</summary>
    public bool EpgEnabled { get; set; } = true;

    /// <summary>Hours between two runs of the guide grabber.</summary>
    public int EpgIntervalHours { get; set; } = 6;

    /// <summary>How many idle tuners the grabber may use at once. Zero leaves the guide to tuners that stream anyway.</summary>
    public int EpgMaxTuners { get; set; } = 1;

    /// <summary>ISO 639-2 codes, comma separated, of the guide languages to prefer. Empty is the server's language, then English.</summary>
    public string? EpgLanguages { get; set; }
}

/// <summary>
/// One device, as an operator types it in: where it is, and optionally a name and how many of
/// its tuners to use. Which satellite is on which input, and which frequencies a cable or an
/// aerial carries, the plugin finds out by tuning.
/// </summary>
public sealed class SatIpDeviceSettings
{
    /// <summary>
    /// IP address or host name, optionally with the RTSP port. May be left empty where
    /// <see cref="DescriptionUrl"/> is given; the host is then the one in that URL.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Where the SAT>IP server's device description is, for a server that does not answer the
    /// SSDP search. Read directly, it gives the server's name and how many tuners of which kind
    /// it has.
    /// </summary>
    public string? DescriptionUrl { get; set; }

    /// <summary>The host this entry names: its address, or else the host of its description URL.</summary>
    public string EffectiveAddress() =>
        !string.IsNullOrWhiteSpace(Address)
            ? Address
            : TryDescriptionUri() is { } description ? description.Host : string.Empty;

    /// <summary>The description URL, where one was given and it is an absolute HTTP address.</summary>
    public Uri? TryDescriptionUri() =>
        Uri.TryCreate(DescriptionUrl?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;

    public string? Name { get; set; }

    /// <summary>How many tuners of the device may be used at once. Zero is all of them.</summary>
    public int MaxTuners { get; set; }
}

/// <summary>A frontend of a DVB card in this machine.</summary>
public sealed class DvbCardSettings
{
    public int Adapter { get; set; }

    public int Frontend { get; set; }

    public string? Name { get; set; }

    public LinuxDvb.DishSettings Dish { get; set; } = new();
}

/// <summary>
/// A satellite position in tenths of a degree, east positive: <c>19.2E</c> is 192,
/// <c>30.0W</c> is -300.
/// </summary>
internal readonly record struct OrbitalPosition(int Tenths)
{
    public static bool TryParse(string? text, out OrbitalPosition position)
    {
        position = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim().Replace("°", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (trimmed.Length < 2)
        {
            return false;
        }

        var hemisphere = char.ToUpperInvariant(trimmed[^1]);
        if (hemisphere is not ('E' or 'W')
            || !decimal.TryParse(trimmed[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var degrees)
            || degrees > 180)
        {
            return false;
        }

        var tenths = (int)Math.Round(degrees * 10);
        position = new OrbitalPosition(hemisphere == 'E' ? tenths : -tenths);

        return true;
    }

    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Math.Abs(Tenths) / 10}.{Math.Abs(Tenths) % 10}{(Tenths >= 0 ? 'E' : 'W')}");
}
