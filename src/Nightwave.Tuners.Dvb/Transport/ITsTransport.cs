using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.Tuning;

namespace Nightwave.Tuners.Dvb.Transport;

/// <summary>
/// One tuner delivering a transport stream: tuned once, told which pids to pass, read until it
/// is disposed. Behind it is an RTSP session or - in tests - a list of packets.
/// </summary>
internal interface ITsTransport : IAsyncDisposable
{
    /// <summary>Tunes and starts the stream with these pids. Fails with a <see cref="TunerTransportException"/>.</summary>
    Task StartAsync(IReadOnlyCollection<int> pids, CancellationToken cancellationToken);

    /// <summary>Changes the pids the tuner passes, without tuning again where the protocol allows.</summary>
    Task SetPidsAsync(IReadOnlyCollection<int> pids, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the next part of the transport stream: whole packets or not, that is the reader's
    /// problem. Zero means the stream ended.
    /// </summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    /// <summary>
    /// What the tuner last said about its signal and how many datagrams went missing, for a scan
    /// that wants to know about a lock before any data comes and for the tuner status. A
    /// transport that cannot tell reports <see cref="TransportStatus.Unknown"/>.
    /// </summary>
    TransportStatus Status => TransportStatus.Unknown;
}

/// <summary>What to tune on a device.</summary>
/// <param name="Tuning">The transponder.</param>
/// <param name="Source">The SAT>IP input (<c>src</c>) for satellite.</param>
internal sealed record TuneRequest(TuningParameters Tuning, int? Source)
{
    /// <summary>For logs and error messages: the transponder.</summary>
    public string Describe() => Tuning.Describe();

    /// <inheritdoc cref="Describe"/>
    public override string ToString() => Describe();
}

internal interface ITsTransportFactory
{
    ITsTransport Create(TunerDevice device, TuneRequest request);
}

/// <summary>
/// A device refused or dropped a stream. English and for the log; the tuner turns it into a
/// translated error that names the channel.
/// </summary>
internal sealed class TunerTransportException : Exception
{
    public TunerTransportException(string message)
        : base(message)
    {
    }

    public TunerTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public TunerTransportException()
    {
    }

    /// <summary>What the tuner reported about its signal when this happened, where it reports it.</summary>
    public TunerSignal? Signal { get; init; }

    /// <summary>True when the device could not be reached at all, so nothing is known about the tuning.</summary>
    public bool Unreachable { get; init; }
}
