namespace Nightwave.Core.Tuning;

/// <summary>
/// A live stream that can say more about itself than what it carries: which device it comes
/// from, how that device receives it, and what is done to it on the way - descrambled, by whom,
/// how well. Implemented by an <see cref="ITunedStream"/> that knows such things; the player's
/// details panel shows them, so that "why does this channel look like that" has an answer on the
/// screen rather than in a log.
/// </summary>
/// <remarks>
/// Asked often while somebody has the panel open, so it answers from what the stream already
/// knows and does no I/O.
/// </remarks>
public interface ITunedStreamDetails
{
    IReadOnlyList<TunedStreamDetailGroup> GetDetails();
}

/// <summary>
/// Live measurements of a stream, for a stream that has any. Asked once a second; answers from
/// what the stream already knows and does no I/O.
/// </summary>
public interface ITunedStreamTelemetry
{
    /// <summary>What the tuner last reported. Null where it has not reported, or there is no tuner.</summary>
    SignalReading? Signal { get; }

    /// <summary>What the network delivered. Null for a transport without datagrams.</summary>
    TransportReading? Transport { get; }

    /// <summary>The device the stream comes from, as it calls itself.</summary>
    string? DeviceName { get; }

    /// <summary>The input of the device.</summary>
    int? Input { get; }

    /// <summary>The front end of the device that is tuned.</summary>
    int? FrontEnd { get; }

    /// <summary>Whether the carrier has pilots, as the tuner found it.</summary>
    bool? Pilots { get; }

    /// <summary>How a scrambled stream is being opened. Null for one that is not scrambled.</summary>
    DescramblingReading? Descrambling { get; }
}

/// <summary>
/// A tuner's report about its signal. Anything a kind of tuner does not measure stays null: a
/// SAT>IP server reports level, lock and quality and nothing else, and no figure is made up for
/// the rest.
/// </summary>
public sealed record SignalReading
{
    public required bool Locked { get; init; }

    /// <summary>Between 0 and 1, on the tuner's own scale.</summary>
    public double? Level { get; init; }

    /// <summary>Between 0 and 1, on the tuner's own scale.</summary>
    public double? Quality { get; init; }

    /// <summary>The level as reported, with its scale: <c>224 / 255</c>.</summary>
    public string? LevelRaw { get; init; }

    /// <summary>The quality as reported, with its scale: <c>15 / 15</c>.</summary>
    public string? QualityRaw { get; init; }

    /// <summary>Signal to noise ratio in dB, from a tuner that measures it.</summary>
    public double? SnrDb { get; init; }

    /// <summary>Bit error rate, from a tuner that measures it.</summary>
    public double? BitErrorRate { get; init; }
}

/// <summary>How a scrambled stream is being opened, as the descrambling server reports it.</summary>
public sealed record DescramblingReading
{
    public required DescramblingState State { get; init; }

    /// <summary>What keeps it from working, in English.</summary>
    public string? Problem { get; init; }

    /// <summary>host:port.</summary>
    public string? Server { get; init; }

    public string? ServerVersion { get; init; }

    public bool Connected { get; init; }

    /// <summary>The conditional access system of the last answer.</summary>
    public int? CaId { get; init; }

    public string? CardSystem { get; init; }

    public int? EcmPid { get; init; }

    public long? ProviderId { get; init; }

    public string? Reader { get; init; }

    /// <summary>Where the answer came from: <c>local</c>, or a peer.</summary>
    public string? Source { get; init; }

    public string? Protocol { get; init; }

    /// <summary>How many sharing hops the answer travelled. Zero is a card on the server itself.</summary>
    public int? Hops { get; init; }

    public long? EcmTimeMs { get; init; }

    public long? EcmTimeFastestMs { get; init; }

    public double? EcmTimeAverageMs { get; init; }

    public long? EcmTimeSlowestMs { get; init; }

    public long EcmsAnswered { get; init; }

    public long EcmsForwarded { get; init; }

    public long OtherSectionsForwarded { get; init; }

    /// <summary>EMMs handed to the server. Stays zero where the server does not ask for them.</summary>
    public long EmmsForwarded { get; init; }

    public long SectionsDropped { get; init; }

    public long KeysReceived { get; init; }

    /// <summary>The algorithm of the last key, for example <c>CSA</c>.</summary>
    public string? Algorithm { get; init; }

    /// <summary>Packets dropped because no key could open them.</summary>
    public long StillScrambledPackets { get; init; }
}

public enum DescramblingState
{
    /// <summary>Scrambled, and no descrambling server is set up.</summary>
    NotConfigured,

    WaitingForKeys,

    Active,

    Problem,
}

/// <summary>How a stream travels to this server.</summary>
public sealed record TransportReading
{
    /// <summary>For example <c>RTP over UDP</c>.</summary>
    public string? Path { get; init; }

    public long DatagramsReceived { get; init; }

    public long DatagramsLost { get; init; }

    public long DatagramsReordered { get; init; }
}

/// <summary>A heading and the lines under it, e.g. "Reception" with signal level and quality.</summary>
public sealed record TunedStreamDetailGroup
{
    /// <summary>Translation key of the heading, relative to the plugin's own catalogue.</summary>
    public required string TitleKey { get; init; }

    public required IReadOnlyList<TunedStreamDetail> Items { get; init; }
}

/// <summary>One line of details.</summary>
public sealed record TunedStreamDetail
{
    /// <summary>Translation key of the label, relative to the plugin's own catalogue.</summary>
    public required string LabelKey { get; init; }

    /// <summary>
    /// The value as data - a device name, a frequency, a count. Shown as it is, unless
    /// <see cref="ValueKey"/> says it is a word to translate.
    /// </summary>
    public required string Value { get; init; }

    /// <summary>
    /// Translation key, relative to the plugin's catalogue, for a value that is a word rather than
    /// data ("active", "no key yet"). <see cref="Value"/> is handed to it as <c>{{value}}</c>.
    /// </summary>
    public string? ValueKey { get; init; }

    public TunedStreamDetailState State { get; init; } = TunedStreamDetailState.Neutral;

    /// <summary>
    /// Whether this line is the operator's business rather than the viewer's: an address on the
    /// network, the card or reader a subscription is descrambled with, the pids of a service.
    /// Somebody watching gets the lines that say how well their picture is coming in; the rest is
    /// shown only to whoever may administer live TV.
    /// </summary>
    public bool IsOperatorOnly { get; init; }
}

/// <summary>How a line is marked: most are just facts, some are good or bad news.</summary>
public enum TunedStreamDetailState
{
    Neutral,
    Good,
    Warning,
    Problem,
}
