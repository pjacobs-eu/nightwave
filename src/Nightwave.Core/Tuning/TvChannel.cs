namespace Nightwave.Core.Tuning;

/// <summary>A channel as a tuner provider reports it.</summary>
public sealed record TvChannel
{
    /// <summary>Identifier inside the providing plugin. Unique together with <see cref="ProviderId"/>.</summary>
    public required string Id { get; init; }

    public required string ProviderId { get; init; }

    public required string Name { get; init; }

    /// <summary>Channel number as broadcast, for example <c>10.1</c>. Sorting key in the UI.</summary>
    public string? Number { get; init; }

    public ChannelKind Kind { get; init; } = ChannelKind.Television;

    public Uri? Logo { get; init; }

    /// <summary>
    /// Guide identifier used to match this channel against EPG data from a separate guide
    /// provider, for example an XMLTV channel id.
    /// </summary>
    public string? GuideId { get; init; }

    /// <summary>True when the channel needs a subscription the server does not have.</summary>
    public bool IsEncrypted { get; init; }

    /// <summary>Where a broadcast channel is transmitted. Null for a web stream.</summary>
    public BroadcastFacts? Broadcast { get; init; }
}

/// <summary>What a scan found out about a broadcast service and its transponder.</summary>
public sealed record BroadcastFacts
{
    /// <summary><c>19.2E</c>, or the network behind a cable or terrestrial input.</summary>
    public required string Source { get; init; }

    public string? ServiceProvider { get; init; }

    public int? ServiceType { get; init; }

    public int NetworkId { get; init; }

    public int TransportStreamId { get; init; }

    public int ServiceId { get; init; }

    public int PmtPid { get; init; }

    public int PcrPid { get; init; }

    public IReadOnlyList<int> Pids { get; init; } = [];

    public IReadOnlyList<int> CaSystems { get; init; } = [];

    public IReadOnlyList<int> EcmPids { get; init; } = [];

    /// <summary><c>DVB-S2</c> and so on.</summary>
    public string? System { get; init; }

    public double? FrequencyMhz { get; init; }

    /// <summary><c>H</c>, <c>V</c>, <c>L</c> or <c>R</c>.</summary>
    public string? Polarization { get; init; }

    public int? SymbolRate { get; init; }

    public string? Modulation { get; init; }

    /// <summary>As SAT>IP writes it: <c>34</c> is 3/4.</summary>
    public string? Fec { get; init; }

    public string? RollOff { get; init; }

    public bool? Pilots { get; init; }

    /// <summary>Tells transponders apart: everything a list of them is grouped by.</summary>
    public string Transponder => $"{Source} {FrequencyMhz} {Polarization} {SymbolRate} {System}";
}

/// <summary>Whether a channel carries video or audio only.</summary>
public enum ChannelKind
{
    Television,
    Radio,
}
