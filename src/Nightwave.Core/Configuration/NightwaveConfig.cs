using System.Text.Json.Serialization;

namespace Nightwave.Core.Configuration;

/// <summary>Everything an operator sets, stored as one JSON file in the data directory.</summary>
public sealed class NightwaveConfig
{
    public List<TunerConfig> Tuners { get; set; } = [];

    /// <summary>Whether the network is searched for SAT>IP servers. Listed ones are used either way.</summary>
    public bool DiscoverSatIp { get; set; } = true;

    public DescramblingConfig Descrambling { get; set; } = new();

    public GuideConfig Guide { get; set; } = new();

    public OutputConfig Output { get; set; } = new();

    public AccessConfig Access { get; set; } = new();

    /// <summary>The language of the interface, such as "de". Null follows the browser.</summary>
    public string? Language { get; set; }
}

/// <summary>What a tuner is. Each kind brings its own backend; hardware tuners join here.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TunerKind>))]
public enum TunerKind
{
    SatIp,
    M3u,

    /// <summary>A frontend of a DVB card in this machine.</summary>
    Dvb,

    /// <summary>A HDHomeRun on the network.</summary>
    HdHomeRun,
}

/// <summary>One tuner as the operator added it.</summary>
public sealed class TunerConfig
{
    /// <summary>Written once; channel ids are built on it, so a provider that moves keeps its bouquets.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public TunerKind Kind { get; set; }

    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    /// <summary>Whether what this tuner delivers goes through the stream repair, where it can.</summary>
    public bool Repair { get; set; }

    public SatIpTunerConfig SatIp { get; set; } = new();

    public M3uTunerConfig M3u { get; set; } = new();

    public DvbTunerConfig Dvb { get; set; } = new();

    public HdHomeRunTunerConfig HdHomeRun { get; set; } = new();


    /// <summary>Web tuners only: everything to this tuner's provider goes through this proxy.</summary>
    public ProxyConfig Proxy { get; set; } = new();

    /// <summary>Whether this kind reaches the internet, and a proxy therefore means anything.</summary>
    [JsonIgnore]
    public bool SupportsProxy => Kind is TunerKind.M3u;

    /// <summary>Only reception over the air has transmission errors to repair; a web stream has none of that kind.</summary>
    [JsonIgnore]
    public bool SupportsRepair => Kind is TunerKind.SatIp or TunerKind.Dvb;

    /// <summary>The proxy this tuner has to use, or null where it goes out directly.</summary>
    [JsonIgnore]
    public ProxyConfig? EffectiveProxy => SupportsProxy && Proxy.Enabled ? Proxy : null;
}

public sealed class SatIpTunerConfig
{
    /// <summary>IP address or host name, optionally with the RTSP port.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>The device description, for a server that does not answer the SSDP search.</summary>
    public string? DescriptionUrl { get; set; }

    /// <summary>How many of its tuners may be used at once. Zero is all of them.</summary>
    public int MaxTuners { get; set; }
}

public sealed class DvbTunerConfig
{
    /// <summary>The number of <c>/dev/dvb/adapterN</c>.</summary>
    public int Adapter { get; set; }

    /// <summary>The number of <c>frontendN</c> on the adapter.</summary>
    public int Frontend { get; set; }

    /// <summary>The LNB's low band oscillator. The defaults are a universal LNB.</summary>
    public int LnbLowMhz { get; set; } = 9750;

    public int LnbHighMhz { get; set; } = 10600;

    public int LnbSwitchMhz { get; set; } = 11700;

    public DvbSwitchKind Switch { get; set; } = DvbSwitchKind.Committed;

    public DvbUnicable Unicable { get; set; }

    /// <summary>The user band of this tuner on a unicable system, counted from zero.</summary>
    public int UnicableSlot { get; set; }

    /// <summary>The frequency of that user band.</summary>
    public int UnicableMhz { get; set; } = 1210;

    /// <summary>The PIN of a protected unicable system. Null is none.</summary>
    public int? UnicablePin { get; set; }

    public DvbRotor Rotor { get; set; }

    /// <summary>The satellites a motor goes to, such as <c>19.2E, 13.0E, 0.8W</c>. For stored positions, in their order.</summary>
    public string? RotorPositions { get; set; }

    /// <summary>Where the dish stands, in degrees, north positive. USALS needs it.</summary>
    public double SiteLatitude { get; set; }

    /// <summary>East positive.</summary>
    public double SiteLongitude { get; set; }

    /// <summary>How long the motor takes for a degree.</summary>
    public int RotorMsPerDegree { get; set; } = 700;
}

/// <summary>What sits between the card and several LNBs.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DvbSwitchKind>))]
public enum DvbSwitchKind
{
    /// <summary>DiSEqC 1.0, up to four inputs.</summary>
    Committed,

    /// <summary>DiSEqC 1.1, up to four inputs.</summary>
    Uncommitted,

    /// <summary>Tone burst, two inputs.</summary>
    ToneBurst,

    /// <summary>One LNB straight on the cable.</summary>
    None,
}

/// <summary>A single cable system, on which each tuner has a user band of its own.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DvbUnicable>))]
public enum DvbUnicable
{
    None,

    /// <summary>EN 50494, Unicable I: up to eight user bands.</summary>
    En50494,

    /// <summary>EN 50607, Unicable II or JESS: up to 32 user bands.</summary>
    En50607,
}

/// <summary>A dish on a motor.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DvbRotor>))]
public enum DvbRotor
{
    None,

    /// <summary>DiSEqC 1.2: positions stored in the motor.</summary>
    StoredPositions,

    /// <summary>USALS: the motor is told the angle, worked out from where the dish stands.</summary>
    Usals,
}

public sealed class HdHomeRunTunerConfig
{
    /// <summary>Host name or address. Empty looks for one on the network.</summary>
    public string? Address { get; set; }

    /// <summary>How many of its tuners may be used at once. Zero is all of them.</summary>
    public int MaxStreams { get; set; }
}

public sealed class M3uTunerConfig
{
    /// <summary>Address of the playlist, or a path on this machine.</summary>
    public string PlaylistUrl { get; set; } = string.Empty;

    /// <summary>Address of an XMLTV feed for it, or a path on this machine.</summary>
    public string? XmltvUrl { get; set; }

    /// <summary>How many streams the provider allows at once.</summary>
    public int MaxStreams { get; set; } = 1;

    public int RefreshHours { get; set; } = 12;

    public string? UserAgent { get; set; }
}

public sealed class ProxyConfig
{
    public bool Enabled { get; set; }

    /// <summary><c>http://</c>, <c>https://</c>, <c>socks4://</c>, <c>socks4a://</c> or <c>socks5://</c>.</summary>
    public string Url { get; set; } = string.Empty;

    public string? Username { get; set; }

    public string? Password { get; set; }
}

public sealed class DescramblingConfig
{
    public bool Enabled { get; set; }

    /// <summary>The machine the descrambling server runs on.</summary>
    public string? Host { get; set; }

    /// <summary>The <c>listen_port</c> of its dvbapi section.</summary>
    public int Port { get; set; } = 9000;
}

public sealed class GuideConfig
{
    /// <summary>Whether the guide is read from the broadcast.</summary>
    public bool BroadcastEnabled { get; set; } = true;

    public int IntervalHours { get; set; } = 6;

    /// <summary>How many idle tuners the grabber may use at once.</summary>
    public int MaxTuners { get; set; } = 1;

    /// <summary>ISO 639-2 codes of the languages to prefer, comma separated.</summary>
    public string? Languages { get; set; }

    /// <summary>How many days ahead an XMLTV file reaches.</summary>
    public int DaysAhead { get; set; } = 7;
}

public sealed class OutputConfig
{
    /// <summary>
    /// The address clients reach this server at, for the links in a playlist. Empty takes the
    /// address the playlist was asked for on.
    /// </summary>
    public string? PublicUrl { get; set; }

    public HdHomeRunEmulationConfig HdHomeRun { get; set; } = new();
}

/// <summary>This server shown to media servers as a HDHomeRun.</summary>
public sealed class HdHomeRunEmulationConfig
{
    /// <summary>The bouquet shown. Null switches the emulation off.</summary>
    public Guid? Bouquet { get; set; }

    /// <summary>How many tuners it claims, which is how many streams a media server opens at once.</summary>
    public int Tuners { get; set; } = 4;

    /// <summary>Its id, kept so that a media server recognises it after a restart.</summary>
    public string? DeviceId { get; set; }
}

public sealed class AccessConfig
{
    /// <summary>PBKDF2 hash of the UI password. Null until one was set on first start.</summary>
    public string? PasswordHash { get; set; }
}
