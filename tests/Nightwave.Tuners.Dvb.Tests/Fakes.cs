using System.Collections.Concurrent;
using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb.Descrambling;
using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.Mpeg;
using Nightwave.Tuners.Dvb.Transport;
using Nightwave.Tuners.Dvb.Tuning;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>
/// A tuner that plays a prepared transport stream in a loop, passing only the pids it was
/// asked for - the way a SAT>IP server does.
/// </summary>
internal sealed class FakeTransport : ITsTransport
{
    private readonly byte[][] _packets;
    private readonly Exception? _startFailure;
    private int _position;
    private volatile HashSet<int> _pids = [];

    public FakeTransport(TuneRequest request, IEnumerable<byte[]> packets, Exception? startFailure = null)
    {
        Request = request;
        _packets = [.. packets];
        _startFailure = startFailure;
    }

    public TuneRequest Request { get; }

    public bool Started { get; private set; }

    public bool Disposed { get; private set; }

    public ConcurrentQueue<IReadOnlyCollection<int>> PidChanges { get; } = new();

    /// <summary>What the tuner reports about its signal; unknown unless a test sets it.</summary>
    public TransportStatus Status { get; set; } = TransportStatus.Unknown;

    /// <summary>A tuner that is tuned but delivers nothing, as one without a lock does: reads wait.</summary>
    public bool Silent { get; init; }

    /// <summary>
    /// Packets delivered once, before the others start going round: what a tuner still has of the
    /// transponder it was tuned to before.
    /// </summary>
    public Queue<byte[]> Leftovers { get; init; } = new();

    public IReadOnlyCollection<int> Pids => _pids;

    public Task StartAsync(IReadOnlyCollection<int> pids, CancellationToken cancellationToken)
    {
        if (_startFailure is not null)
        {
            throw _startFailure;
        }

        _pids = [.. pids];
        Started = true;

        return Task.CompletedTask;
    }

    public Task SetPidsAsync(IReadOnlyCollection<int> pids, CancellationToken cancellationToken)
    {
        _pids = [.. pids];
        PidChanges.Enqueue(pids);

        return Task.CompletedTask;
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        while (true)
        {
            // Paced, as a real tuner is: a few packets at a time.
            await Task.Delay(1, cancellationToken);

            if (Silent)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            if (Disposed || _packets.Length == 0)
            {
                return 0;
            }

            var written = 0;
            var pids = _pids;

            while (Leftovers.Count > 0 && written < 14 * TransportPacket.Size)
            {
                var leftover = Leftovers.Dequeue();

                if (pids.Contains(TransportPacket.Pid(leftover)))
                {
                    leftover.CopyTo(buffer[written..]);
                    written += TransportPacket.Size;
                }
            }

            if (written > 0)
            {
                return written;
            }

            for (var tried = 0; tried < _packets.Length && written + TransportPacket.Size <= buffer.Length && written < 14 * TransportPacket.Size; tried++)
            {
                var packet = _packets[_position];
                _position = (_position + 1) % _packets.Length;

                if (pids.Contains(TransportPacket.Pid(packet)))
                {
                    packet.CopyTo(buffer[written..]);
                    written += TransportPacket.Size;
                }
            }

            if (written > 0)
            {
                return written;
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Hands out <see cref="FakeTransport"/>s: a stream per transponder, a refusal for the rest.
/// With <see cref="ReportSignal"/> it behaves like a tuner that sends RTCP reports: a locked
/// transponder reports a lock, a frequency with a carrier but the wrong settings reports its
/// level and delivers nothing, and anything else is refused with level 0.
/// </summary>
internal sealed class FakeTransportFactory : ITsTransportFactory
{
    private readonly List<(TuningParameters Tuning, IEnumerable<byte[]> Packets, int? Source)> _transponders = [];
    private readonly Dictionary<int, int> _carriers = [];

    public List<FakeTransport> Created { get; } = [];

    /// <summary>Whether transports report a signal status.</summary>
    public bool ReportSignal { get; init; }

    /// <summary>Whether symbol rate and modulation have to match too, as they do for a real cable tuner.</summary>
    public bool Exact { get; init; }

    public FakeTransportFactory Add(TuningParameters tuning, IEnumerable<byte[]> packets, int? source = null)
    {
        _transponders.Add((tuning, packets, source));

        return this;
    }

    /// <summary>A frequency where the tuner sees a signal of this level, whatever it is tuned with.</summary>
    public FakeTransportFactory AddCarrier(int frequencyKhz, int level)
    {
        _carriers[frequencyKhz] = level;

        return this;
    }

    public ITsTransport Create(TunerDevice device, TuneRequest request)
    {
        var found = _transponders.FirstOrDefault(entry =>
            entry.Tuning.System == request.Tuning.System
            && entry.Tuning.IsSameTransponder(request.Tuning)
            && (entry.Source is null || entry.Source == request.Source)
            && (!Exact || (entry.Tuning.SymbolRate == request.Tuning.SymbolRate && entry.Tuning.Modulation == request.Tuning.Modulation)));

        FakeTransport transport;

        if (found.Packets is not null)
        {
            transport = new FakeTransport(request, found.Packets)
            {
                Status = ReportSignal
                    ? new TransportStatus { Signal = new TunerSignal { Level = 200, Locked = true, Quality = 15 }, SignalReports = 1 }
                    : TransportStatus.Unknown,
            };
        }
        else if (ReportSignal && _carriers.TryGetValue(request.Tuning.FrequencyKhz, out var level))
        {
            transport = new FakeTransport(request, [])
            {
                Silent = true,
                Status = new TransportStatus { Signal = new TunerSignal { Level = level, Locked = false, Quality = 0 }, SignalReports = 1 },
            };
        }
        else if (ReportSignal)
        {
            var signal = new TunerSignal { Level = 0, Locked = false, Quality = 0 };
            transport = new FakeTransport(request, [], new TunerTransportException($"{signal.Describe()} after 0 seconds.") { Signal = signal });
        }
        else
        {
            transport = new FakeTransport(request, [], new TunerTransportException($"{device.Label} answered SETUP for {request.Describe()} with RTSP 408: it did not lock on the signal in time."));
        }

        lock (Created)
        {
            Created.Add(transport);
        }

        return transport;
    }
}

/// <summary>A conditional access server that answers at once and records what it was asked.</summary>
internal sealed class FakeDescramblerFactory : IServiceDescramblerFactory
{
    public bool IsConfigured { get; set; } = true;

    public List<FakeDescrambler> Opened { get; } = [];

    /// <summary>Whether a descrambler opened from here starts without keys, as a card that has not answered yet.</summary>
    public bool HoldKeys { get; set; }

    public Task<IServiceDescrambler> OpenAsync(ServiceDescramblingRequest request, CancellationToken cancellationToken)
    {
        var descrambler = new FakeDescrambler(request) { Keys = !HoldKeys };

        lock (Opened)
        {
            Opened.Add(descrambler);
        }

        return Task.FromResult<IServiceDescrambler>(descrambler);
    }
}

/// <summary>
/// Clears the scrambling bits of every packet it is given, as a real one would after
/// descrambling, and asks for one ECM pid.
/// </summary>
internal sealed class FakeDescrambler : IServiceDescrambler, IServiceDescramblerDiagnostics
{
    private DescramblingStatus _status = new() { Server = "192.0.2.30:9000", Connected = true, HasKeys = true };

    private IReadOnlyCollection<int> _required = [0x0200];

    public FakeDescrambler(ServiceDescramblingRequest request)
    {
        Request = request;
    }

    public ServiceDescramblingRequest Request { get; }

    public IReadOnlyCollection<int> RequiredPids => _required;

    public event EventHandler? RequiredPidsChanged;

    /// <summary>Whether the card has answered. Set to false for one that has not yet.</summary>
    public bool Keys { get; set; } = true;

    public bool HasKeys => Keys;

    public int LargestBatch { get; private set; }

    public DescramblingStatus Status => _status;

    public event EventHandler? StatusChanged;

    public string DescribeMissingKey(int pid, bool odd) => "a fake has every key";

    public void Report(TunerException? problem)
    {
        _status = _status with { Problem = problem };
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public ConcurrentBag<int> SeenPids { get; } = [];

    public List<byte[]> ProgramMapUpdates { get; } = [];

    public bool Disposed { get; private set; }

    /// <summary>Leaves packets of this pid scrambled, as a descrambler without a key for them does.</summary>
    public int? KeylessPid { get; set; }

    /// <summary>What trying a key says. A fake's keys fit unless a test says otherwise.</summary>
    public KeyProbe ProbeResult { get; set; } = KeyProbe.Fits;

    public int Probes { get; private set; }

    /// <summary>How many bytes were handed over for their sections.</summary>
    public long SectionBytes { get; private set; }

    public KeyProbe Probe(ReadOnlySpan<byte> packet)
    {
        Probes++;

        return ProbeResult;
    }

    public void ForwardSections(ReadOnlySpan<byte> packets) => SectionBytes += packets.Length;

    public void Descramble(Span<byte> packets) => Process(packets);

    public void Process(Span<byte> packets)
    {
        LargestBatch = Math.Max(LargestBatch, packets.Length / TransportPacket.Size);

        for (var offset = 0; offset + TransportPacket.Size <= packets.Length; offset += TransportPacket.Size)
        {
            var pid = TransportPacket.Pid(packets[offset..]);
            SeenPids.Add(pid);

            if (pid != KeylessPid)
            {
                packets[offset + 3] &= 0x3F;
            }
        }
    }

    public Task UpdateProgramMapAsync(ReadOnlyMemory<byte> programMapSection, CancellationToken cancellationToken)
    {
        lock (ProgramMapUpdates)
        {
            ProgramMapUpdates.Add(programMapSection.ToArray());
        }

        return Task.CompletedTask;
    }

    public void Require(params int[] pids)
    {
        _required = pids;
        RequiredPidsChanged?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;

        return ValueTask.CompletedTask;
    }
}

/// <summary>A network with exactly the devices a test puts on it.</summary>
internal sealed class FakeDeviceDiscovery : IDeviceDiscovery
{
    public List<DiscoveredSatIpServer> SatIpServers { get; } = [];

    public Task<IReadOnlyList<DiscoveredSatIpServer>> FindSatIpServersAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DiscoveredSatIpServer>>(SatIpServers);

    /// <summary>Descriptions that can be read by URL, from servers that do not answer a search.</summary>
    public Dictionary<Uri, SatIpDeviceDescription> Descriptions { get; } = [];

    public Task<DiscoveredSatIpServer?> ProbeSatIpServerAsync(string host, CancellationToken cancellationToken) =>
        Task.FromResult(SatIpServers.FirstOrDefault(server => server.Host == host));

    public Task<DiscoveredSatIpServer?> DescribeSatIpServerAsync(Uri descriptionUrl, CancellationToken cancellationToken) =>
        Task.FromResult(Descriptions.TryGetValue(descriptionUrl, out var description)
            ? new DiscoveredSatIpServer(descriptionUrl.Host, description)
            : null);

    public static DiscoveredSatIpServer SatIp(string host, string capabilities) =>
        new(host, new SatIpDeviceDescription
        {
            FriendlyName = "Test SAT>IP",
            Tuners = SatIpDeviceDescription.ParseCapabilities(capabilities),
        });
}

/// <summary>Detected inputs, the way a test wants them.</summary>
internal static class TestInputs
{
    /// <summary>A cache in memory in which the device's inputs were just detected.</summary>
    public static InputCache Detected(string deviceId, params (int Source, int Tenths)[] inputs)
    {
        var cache = new InputCache(null);
        cache.SetAsync(
            deviceId,
            new DeviceInputs
            {
                DetectedAt = DateTimeOffset.UtcNow,
                Inputs = [.. inputs.Select(input => new DetectedInput { Source = input.Source, Position = input.Tenths, Evidence = "nit" })],
            },
            CancellationToken.None).GetAwaiter().GetResult();

        return cache;
    }
}

/// <summary>A progress that keeps every report, synchronously.</summary>
internal sealed class RecordingProgress : IProgress<ScanProgress>
{
    public List<ScanProgress> Reports { get; } = [];

    public void Report(ScanProgress value)
    {
        lock (Reports)
        {
            Reports.Add(value);
        }
    }
}
