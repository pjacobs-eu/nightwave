namespace Nightwave.Core.Tuning;

/// <summary>
/// A tuner provider that finds its channels by tuning rather than by reading a list: a SAT>IP
/// server, a network tuner, a DVB card. Registered next to its <see cref="ITunerProvider"/>
/// under the same provider id.
/// </summary>
/// <remarks>
/// The provider does the scanning; the server runs it. One scan per provider at a time, started
/// from the admin UI, watched there while it runs, and its outcome kept for somebody who comes
/// back later. What a scan found is the provider's to keep: afterwards it is simply what
/// <see cref="ITunerProvider.GetChannelsAsync"/> returns.
/// </remarks>
public interface IChannelScanner
{
    /// <summary>The same id as the tuner provider whose channels this scans for.</summary>
    string ProviderId { get; }

    /// <summary>
    /// What can be scanned right now: one entry per device and signal source, for example a
    /// SAT>IP server's first satellite input, or a network tuner's cable input. Found by asking
    /// the network and the tuner settings, so this may take a moment.
    /// </summary>
    Task<IReadOnlyList<ScanTarget>> GetTargetsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Scans one target and keeps what it finds. Reports progress as it goes and returns what it
    /// found once it is done. Cancelling keeps the channels found so far only where the request
    /// said to merge; a replacing scan that is cancelled changes nothing.
    /// </summary>
    Task<ScanResult> ScanAsync(
        ScanRequest request,
        IProgress<ScanProgress> progress,
        CancellationToken cancellationToken = default);
}

/// <summary>Something a scanner can scan: a device, and the signal source on it.</summary>
public sealed record ScanTarget
{
    /// <summary>Stable within the provider, passed back in <see cref="ScanRequest.TargetId"/>.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// What the device calls itself and which input this is, for example
    /// <c>Digibit R1 (192.168.1.20) - Astra 19.2°E</c>. Shown as it is: it names hardware, and
    /// hardware is not translated.
    /// </summary>
    public required string Name { get; init; }

    public required DeliverySystem DeliverySystem { get; init; }

    /// <summary>
    /// How many channels the provider holds from this target already, so the UI can say whether
    /// a scan would be the first one or a repeat.
    /// </summary>
    public int KnownChannels { get; init; }

    /// <summary>When this target was last scanned to the end, if ever.</summary>
    public DateTimeOffset? LastScannedAt { get; init; }

    /// <summary>English, untranslated reason when the target cannot be scanned right now.</summary>
    public string? Error { get; init; }
}

/// <summary>How a target receives its signal.</summary>
public enum DeliverySystem
{
    Satellite,
    Cable,
    Terrestrial,

    /// <summary>A device that tunes by itself and hands out a finished channel list.</summary>
    Network,
}

/// <summary>What to scan and what to do with what is found.</summary>
public sealed record ScanRequest
{
    public required string TargetId { get; init; }

    /// <summary>
    /// Whether channels this target had before and no longer finds are removed. A full scan
    /// replaces; a scan for a new transponder merges.
    /// </summary>
    public ScanMode Mode { get; init; } = ScanMode.Replace;

    /// <summary>Whether channels that are scrambled are kept. They are, unless somebody says otherwise.</summary>
    public bool IncludeEncrypted { get; init; } = true;

    /// <summary>Whether radio channels are kept.</summary>
    public bool IncludeRadio { get; init; } = true;
}

public enum ScanMode
{
    /// <summary>What the target offers afterwards is what it scanned.</summary>
    Replace,

    /// <summary>What it scanned is added to what it had; nothing is removed.</summary>
    Merge,
}

/// <summary>How far a scan has got. Reported often; every field is a snapshot, not a delta.</summary>
public sealed record ScanProgress
{
    /// <summary>Between 0 and 1. A scan that learns of more transponders as it goes may move back.</summary>
    public required double Fraction { get; init; }

    /// <summary>
    /// What is being tuned now, as the device would describe it, for example
    /// <c>11836 MHz H 27500 DVB-S2</c>. Not translated, for the same reason as a target's name.
    /// </summary>
    public string? Current { get; init; }

    public int TransponderCount { get; init; }

    public int TranspondersDone { get; init; }

    public int TelevisionFound { get; init; }

    public int RadioFound { get; init; }

    /// <summary>Of the channels found, how many are scrambled.</summary>
    public int EncryptedFound { get; init; }
}

/// <summary>What a scan ended with.</summary>
public sealed record ScanResult
{
    public required int TelevisionFound { get; init; }

    public required int RadioFound { get; init; }

    public required int EncryptedFound { get; init; }

    /// <summary>Channels the target offered before and does not any more. Zero for a merge.</summary>
    public int Removed { get; init; }

    /// <summary>
    /// Transponders that were in the plan and could not be tuned or yielded nothing, each with the
    /// reason in English. A scan that finished with some of these still finished.
    /// </summary>
    public IReadOnlyList<string> Failures { get; init; } = [];
}
