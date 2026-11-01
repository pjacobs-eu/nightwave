using Nightwave.Core.Tuning;

namespace Nightwave.Tuners.Dvb.Descrambling;

/// <summary>
/// Why a service plays or does not: which server, which card system and reader, which
/// algorithm, and what went wrong. Implemented by what <see cref="IServiceDescramblerFactory"/>
/// returns; the tuner reads it with a type test and surfaces <see cref="DescramblingStatus.Problem"/>.
/// </summary>
internal interface IServiceDescramblerDiagnostics
{
    DescramblingStatus Status { get; }

    /// <summary>Raised from any thread when <see cref="Status"/> changed in a way worth showing.</summary>
    event EventHandler? StatusChanged;

    /// <summary>
    /// Why there is no key for packets of this parity right now, in the terms that tell the
    /// causes apart: when each key last came, and when an ECM of each table id was last seen on
    /// the air and last handed to the server.
    /// </summary>
    string DescribeMissingKey(int pid, bool odd);
}

internal sealed record DescramblingStatus
{
    /// <summary>host:port of the descrambling server.</summary>
    public required string Server { get; init; }

    public bool Connected { get; init; }

    /// <summary>The server's own version string, from the handshake.</summary>
    public string? ServerVersion { get; init; }

    public bool HasKeys { get; init; }

    /// <summary>The algorithm of the last key, e.g. "CSA", "CSA (iCAM mode 4)", "AES-128-CBC".</summary>
    public string? Algorithm { get; init; }

    /// <summary>The last ECM the server answered, as it reported it.</summary>
    public EcmAnswer? LastEcm { get; init; }

    /// <summary>How many ECMs the server answered, and how long it took: fastest, total, slowest.</summary>
    public EcmTimes EcmTimes { get; init; } = new();

    /// <summary>Control words received.</summary>
    public long KeysReceived { get; init; }

    /// <summary>ECM sections forwarded to the server so far.</summary>
    public long EcmSectionsForwarded { get; init; }

    /// <summary>All other sections forwarded (CAT, EMM, ...).</summary>
    public long OtherSectionsForwarded { get; init; }

    /// <summary>Of those, the EMMs: the card's subscription updates, which only flow where the server asks for them.</summary>
    public long EmmSectionsForwarded { get; init; }

    /// <summary>Filter data the server did not get because the connection could not keep up.</summary>
    public long SectionsDropped { get; init; }

    /// <summary>Set while something keeps the service from being descrambled; null when all is well.</summary>
    public TunerException? Problem { get; init; }
}

/// <summary>Answer times of the ECMs of one service, in milliseconds.</summary>
internal sealed record EcmTimes(long Count = 0, long Total = 0, uint Fastest = 0, uint Slowest = 0)
{
    public EcmTimes With(uint milliseconds) => new(
        Count + 1,
        Total + milliseconds,
        Count == 0 ? milliseconds : Math.Min(Fastest, milliseconds),
        Math.Max(Slowest, milliseconds));
}

/// <summary>What the server's ECM_INFO said about the last answered ECM.</summary>
internal sealed record EcmAnswer(
    int CaId,
    int Pid,
    uint ProviderId,
    uint EcmTimeMilliseconds,
    string CardSystem,
    string Reader,
    string Source,
    string Protocol,
    int Hops);
