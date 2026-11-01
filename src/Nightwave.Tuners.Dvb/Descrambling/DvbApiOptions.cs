namespace Nightwave.Tuners.Dvb.Descrambling;

/// <summary>Where the dvbapi options come from; the tuner settings.</summary>
internal interface IDvbApiOptionsSource
{
    /// <summary>The current options, or null when descrambling is switched off.</summary>
    Task<DvbApiOptions?> GetAsync(CancellationToken cancellationToken);
}

/// <summary>The descrambling server to ask for control words: its <c>[dvbapi] listen_port</c>.</summary>
internal sealed record DvbApiOptions
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    /// <summary>How this client introduces itself; the server logs it and shows it in its web interface.</summary>
    public string ClientName { get; init; } = "Nightwave";
}

/// <summary>Timeouts and intervals, fixed in production and shortened by the tests.</summary>
internal sealed record DvbApiTimings
{
    public static DvbApiTimings Default { get; } = new();

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long the plain greeting waits for an answer before the framed one is tried, see
    /// <see cref="DvbApiConnection"/>.
    /// </summary>
    public TimeSpan PlainGreetingTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How long the server has to answer the greeting before the port is taken to be something else.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a service may go without its first control word before that is reported.</summary>
    public TimeSpan ControlWordTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan ReconnectInitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan ReconnectMaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How old the cached options may get before <c>IsConfigured</c> refreshes them in the background.</summary>
    public TimeSpan OptionsRefreshInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long <c>IsConfigured</c> waits for the very first load of the options.</summary>
    public TimeSpan InitialOptionsWait { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>The dvbapi protocol version this client announces, see <see cref="DvbApiProtocol.DefaultProtocolVersion"/>.</summary>
    public int ProtocolVersion { get; init; } = DvbApiProtocol.DefaultProtocolVersion;
}
