using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.Transport;

namespace Nightwave.Tuners.Dvb.LinuxDvb;

/// <summary>
/// A tuner on a card in this machine, through the Linux DVB API: the frontend is tuned, the demux
/// passes the wanted pids to the DVR device as a transport stream, and that is read on a thread
/// of its own, because a card delivers in real time whatever the rest of the process is doing.
/// </summary>
internal sealed partial class LinuxDvbTransport : ITsTransport
{
    private const int Errno2NoEntry = 2;
    private const int Errno13Denied = 13;
    private const int Errno16Busy = 16;
    private const int Errno11Again = 11;
    private const int Errno75Overflow = 75;
    private const int BufferBytes = 8 * 1024 * 1024;
    private const int ReadBytes = 188 * 512;
    private const int Settle = 20;
    private static readonly TimeSpan SignalEvery = TimeSpan.FromMilliseconds(500);

    /// <summary>Only one tuner may talk on a single cable at a time, or the commands run into each other.</summary>
    private static readonly SemaphoreSlim SingleCable = new(1, 1);

    /// <summary>Where each motor was sent last, by adapter, so that the next move is timed from there.</summary>
    private static readonly ConcurrentDictionary<int, int> Rotors = new();

    private readonly TunerDevice _device;
    private readonly LocalDvbCard _card;
    private readonly TuneRequest _request;
    private readonly IDvbKernel _kernel;
    private readonly ILogger _logger;
    private readonly ChunkQueue _queue = new(4096);
    private readonly Lock _pidGate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[] _statistics = DvbApi.Properties(
    [
        (DvbApi.StatSignalStrength, 0),
        (DvbApi.StatCarrierToNoise, 0),
        (DvbApi.StatPostErrorBits, 0),
        (DvbApi.StatPostTotalBits, 0),
    ]);

    private HashSet<int> _pids = [];
    private int _frontend = -1;
    private int _demux = -1;
    private int _dvr = -1;
    private bool _filtering;
    private Thread? _reader;
    private Task? _watcher;
    private volatile TunerSignal? _signal;
    private long _reports;
    private long _reads;
    private long _overflows;
    private int _disposed;

    public LinuxDvbTransport(TunerDevice device, LocalDvbCard card, TuneRequest request, IDvbKernel kernel, ILogger logger)
    {
        _device = device;
        _card = card;
        _request = request;
        _kernel = kernel;
        _logger = logger;
    }

    public TransportStatus Status => new()
    {
        Signal = _signal,
        SignalReports = Interlocked.Read(ref _reports),
        DatagramsReceived = Interlocked.Read(ref _reads),
        DatagramsLost = Interlocked.Read(ref _overflows),
        Path = StreamPath.LocalCard,
    };

    public async Task StartAsync(IReadOnlyCollection<int> pids, CancellationToken cancellationToken)
    {
        var tuning = DvbTuning.For(_request.Tuning, _request.Source, _card.Dish);

        _frontend = Open(_card.FrontendPath, writable: true);
        _demux = Open(_card.DemuxPath, writable: true);
        _dvr = Open(_card.DvrPath, writable: false);

        // The buffer of the DVR device is what lies between the card and a busy machine.
        _ = _kernel.Control(_dvr, DvbApi.DmxSetBufferSize, BufferBytes);

        await PrepareLnbAsync(tuning, cancellationToken).ConfigureAwait(false);

        Check(_kernel.Properties(_frontend, DvbApi.FeSetProperty, DvbApi.Properties([(DvbApi.Clear, 0)])), "clear the frontend");
        Check(_kernel.Properties(_frontend, DvbApi.FeSetProperty, DvbApi.Properties(tuning.Properties)), $"tune {_request.Describe()}");

        Apply(pids.ToHashSet());

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = $"dvb {_device.Host}" };
        _reader.Start();
        _watcher = WatchSignalAsync(_stop.Token);

        LogTuned(_device.Name, _request);
    }

    public Task SetPidsAsync(IReadOnlyCollection<int> pids, CancellationToken cancellationToken)
    {
        Apply(pids.ToHashSet());

        return Task.CompletedTask;
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => _queue.ReadAsync(buffer, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);

        if (_watcher is { } watcher)
        {
            await watcher.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        _reader?.Join(TimeSpan.FromSeconds(2));
        _queue.Complete();

        foreach (var descriptor in new[] { _dvr, _demux, _frontend })
        {
            if (descriptor >= 0)
            {
                _kernel.Close(descriptor);
            }
        }

        _stop.Dispose();
    }

    private int Open(string path, bool writable)
    {
        var descriptor = _kernel.Open(path, writable);

        if (descriptor >= 0)
        {
            return descriptor;
        }

        throw new TunerTransportException(-descriptor switch
        {
            Errno2NoEntry => $"{path} does not exist. Check that the card's driver is loaded, and that a container was given the device.",
            Errno13Denied => $"Nightwave may not open {path}. Add its user to the group of the device, usually 'video'.",
            Errno16Busy => $"{path} is in use by another program. Stop it, or leave this card to it.",
            var error => string.Create(CultureInfo.InvariantCulture, $"{path} could not be opened: error {error}."),
        })
        {
            Unreachable = true,
        };
    }

    private void Check(int result, string what)
    {
        if (result < 0)
        {
            throw new TunerTransportException(string.Create(CultureInfo.InvariantCulture, $"{_device.Label} could not {what}: error {-result}."));
        }
    }

    /// <summary>Voltage first, then the switch, then the band, each given a moment: an LNB and a switch are slow.</summary>
    private async Task PrepareLnbAsync(DvbTuning tuning, CancellationToken cancellationToken)
    {
        if (tuning.Voltage is not { } voltage)
        {
            return;
        }

        if (tuning.Unicable is { } unicable)
        {
            Check(_kernel.Control(_frontend, DvbApi.FeSetTone, DvbApi.ToneOff), "switch the 22 kHz tone off");
            await SendUnicableAsync(unicable, cancellationToken).ConfigureAwait(false);

            return;
        }

        Check(_kernel.Control(_frontend, DvbApi.FeSetTone, DvbApi.ToneOff), "switch the 22 kHz tone off");
        Check(_kernel.Control(_frontend, DvbApi.FeSetVoltage, voltage), "set the LNB voltage");
        await Task.Delay(Settle, cancellationToken).ConfigureAwait(false);

        foreach (var command in tuning.Commands)
        {
            Check(_kernel.Control(_frontend, DvbApi.FeSendDiseqc, command), "send the DiSEqC command");
            await Task.Delay(Settle, cancellationToken).ConfigureAwait(false);
        }

        if (tuning.Burst is { } burst)
        {
            Check(_kernel.Control(_frontend, DvbApi.FeSendBurst, burst), "send the tone burst");
            await Task.Delay(Settle, cancellationToken).ConfigureAwait(false);
        }

        Check(_kernel.Control(_frontend, DvbApi.FeSetTone, tuning.Tone!.Value), "set the 22 kHz tone");
        await Task.Delay(Settle, cancellationToken).ConfigureAwait(false);

        if (tuning.RotorTenths is { } target)
        {
            await WaitForRotorAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Asks the user band for the transponder: the command goes at 18 V, the signal comes at 13 V.</summary>
    private async Task SendUnicableAsync(byte[] command, CancellationToken cancellationToken)
    {
        await SingleCable.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Check(_kernel.Control(_frontend, DvbApi.FeSetVoltage, DvbApi.Voltage18), "set the LNB voltage");
            await Task.Delay(Settle, cancellationToken).ConfigureAwait(false);
            Check(_kernel.Control(_frontend, DvbApi.FeSendDiseqc, command), "send the unicable command");
            await Task.Delay(Settle, cancellationToken).ConfigureAwait(false);
            Check(_kernel.Control(_frontend, DvbApi.FeSetVoltage, DvbApi.Voltage13), "set the LNB voltage");
            await Task.Delay(Settle, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SingleCable.Release();
        }
    }

    /// <summary>A motor reports nothing back, so it is given the time it needs.</summary>
    private async Task WaitForRotorAsync(int target, CancellationToken cancellationToken)
    {
        int? from = Rotors.TryGetValue(_card.Adapter, out var last) ? last : null;

        if (from == target)
        {
            return;
        }

        Rotors[_card.Adapter] = target;
        await Task.Delay(DvbTuning.RotorTravel(from, target, _card.Dish.RotorMsPerDegree), cancellationToken).ConfigureAwait(false);
    }

    private void Apply(HashSet<int> wanted)
    {
        lock (_pidGate)
        {
            foreach (var pid in wanted.Except(_pids))
            {
                if (_filtering)
                {
                    Check(_kernel.Control(_demux, DvbApi.DmxAddPid, Pid(pid)), $"add pid {pid}");
                }
                else
                {
                    // The first pid sets the filter up; the others join it.
                    Check(_kernel.Control(_demux, DvbApi.DmxSetPesFilter, DvbApi.PesFilter(pid)), $"filter pid {pid}");
                    _filtering = true;
                }
            }

            foreach (var pid in _pids.Except(wanted))
            {
                Check(_kernel.Control(_demux, DvbApi.DmxRemovePid, Pid(pid)), $"remove pid {pid}");
            }

            _pids = wanted;
        }
    }

    private static byte[] Pid(int pid) => [(byte)pid, (byte)(pid >> 8)];

    private void ReadLoop()
    {
        var buffer = new byte[ReadBytes];

        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (!_kernel.WaitReadable(_dvr, 200))
                {
                    continue;
                }

                var count = _kernel.Read(_dvr, buffer);

                if (count > 0)
                {
                    Interlocked.Increment(ref _reads);
                    _queue.Write(buffer.AsSpan(0, count));
                }
                else if (count == -Errno75Overflow)
                {
                    // The buffer ran over. Reading on is the way out, and the gap shows in the stream.
                    Interlocked.Increment(ref _overflows);
                }
                else if (count < 0 && count != -Errno11Again)
                {
                    throw new TunerTransportException(string.Create(CultureInfo.InvariantCulture, $"Reading {_card.DvrPath} failed: error {-count}."));
                }
            }

            _queue.Complete();
        }
        catch (TunerTransportException error)
        {
            _queue.Complete(error);
        }
    }

    private async Task WatchSignalAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SignalEvery);

        try
        {
            do
            {
                _signal = ReadSignal();
                Interlocked.Increment(ref _reports);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
    }

    private TunerSignal ReadSignal()
    {
        var status = new byte[4];

        Check(_kernel.Control(_frontend, DvbApi.FeReadStatus, status), "read its status");

        (byte Scale, long Value)? Stat(int index) =>
            DvbApi.FirstStat(_statistics.AsSpan(index * DvbApi.PropertySize, DvbApi.PropertySize));

        // Older drivers know none of this. A lock is still a lock.
        var measured = _kernel.Properties(_frontend, DvbApi.FeGetProperty, _statistics) == 0;

        return DvbTuning.Signal(
            BitConverter.ToUInt32(status),
            measured ? Stat(0) : null,
            measured ? Stat(1) : null,
            measured && Stat(2) is { Scale: DvbApi.ScaleCounter } errors ? errors.Value : null,
            measured && Stat(3) is { Scale: DvbApi.ScaleCounter } total ? total.Value : null);
    }

    [LoggerMessage(EventId = 9880, Level = LogLevel.Information, Message = "Tuned {Device} to {Transponder}.")]
    private partial void LogTuned(string device, TuneRequest transponder);
}
