namespace Nightwave.Tuners.Dvb.Descrambling;

/// <summary>
/// Opens a scrambled service through the operator's own card. The tuner asks this once per
/// tuned channel that needs it; what comes back sees every packet the tuner delivers.
/// </summary>
internal interface IServiceDescramblerFactory
{
    /// <summary>
    /// Whether a conditional access server is configured at all. Where it is not, a scrambled
    /// channel fails with an error that says so rather than playing noise.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Hands the service to the conditional access server and returns what descrambles it. Fails
    /// with a <see cref="Nightwave.Core.Tuning.TunerException"/> naming the server when it
    /// cannot be reached or refuses the service.
    /// </summary>
    Task<IServiceDescrambler> OpenAsync(ServiceDescramblingRequest request, CancellationToken cancellationToken);
}

/// <summary>The service to descramble, as the tuner found it.</summary>
internal sealed record ServiceDescramblingRequest
{
    /// <summary>The program number of the service in its transport stream.</summary>
    public required int ServiceId { get; init; }

    /// <summary>
    /// The whole program map section of the service, table header to CRC, exactly as it was
    /// received. The CA descriptors in it are what the card is asked about.
    /// </summary>
    public required ReadOnlyMemory<byte> ProgramMapSection { get; init; }

    public required int ProgramMapPid { get; init; }

    /// <summary>
    /// A number unique among the services descrambled at the same time, which the dvbapi protocol
    /// calls the adapter and demux index. Given out by the tuner, freed when this is disposed.
    /// </summary>
    public required int Slot { get; init; }

    /// <summary>Transport stream id and original network id, for the CA PMT's descriptors.</summary>
    public required int TransportStreamId { get; init; }

    public required int OriginalNetworkId { get; init; }

    /// <summary>For the log and for the error message somebody reads.</summary>
    public required string ChannelName { get; init; }

    /// <summary>The ECMs the tuner kept before the server asked for them, handed over when it does.</summary>
    public RecentEcms? RecentEcms { get; init; }
}

/// <summary>What trying a packet against its key said.</summary>
internal enum KeyProbe
{
    /// <summary>Nothing to go by: too little payload, or a pid the server switched off.</summary>
    NotApplicable,

    /// <summary>The key turns the packet into the start of a PES unit.</summary>
    Fits,

    /// <summary>There is no key for its parity, or the one there is does not fit.</summary>
    Wrong,
}

/// <summary>
/// One service being descrambled. Fed the transport stream as it arrives, and changes it in
/// place.
/// </summary>
/// <remarks>
/// Not thread safe: the tuner calls <see cref="Process"/> from the one loop that reads the
/// stream, and nothing else calls it.
/// </remarks>
internal interface IServiceDescrambler : IAsyncDisposable
{
    /// <summary>
    /// Packet ids the conditional access server wants to see besides the service's own - its
    /// ECM streams, and the EMM stream where it asks for one. The tuner adds these to what it
    /// receives and removes them again when they go.
    /// </summary>
    IReadOnlyCollection<int> RequiredPids { get; }

    /// <summary>Raised when <see cref="RequiredPids"/> changed. Raised from any thread.</summary>
    event EventHandler? RequiredPidsChanged;

    /// <summary>
    /// True once the first control word has arrived. Until then, scrambled packets are passed on
    /// as they are, and a player shows nothing of them.
    /// </summary>
    bool HasKeys { get; }

    /// <summary>
    /// Takes whole 188 byte transport stream packets. Sections on the pids the server asked for
    /// are forwarded to it; scrambled payloads are descrambled in place with the current key,
    /// and their scrambling control bits cleared.
    /// </summary>
    void Process(Span<byte> packets);

    /// <summary>The first half of <see cref="Process"/>: forwards the sections, touches nothing.</summary>
    void ForwardSections(ReadOnlySpan<byte> packets);

    /// <summary>The second half of <see cref="Process"/>: descrambles in place.</summary>
    void Descramble(Span<byte> packets);

    /// <summary>
    /// Tries a scrambled packet that starts a PES unit against the key of its parity, on a copy.
    /// The key fits when a start code comes out.
    /// </summary>
    KeyProbe Probe(ReadOnlySpan<byte> packet);

    /// <summary>
    /// Tells the conditional access server that the program map changed - a new ECM pid, a new
    /// audio track - so it can ask for what it needs now.
    /// </summary>
    Task UpdateProgramMapAsync(ReadOnlyMemory<byte> programMapSection, CancellationToken cancellationToken);
}
