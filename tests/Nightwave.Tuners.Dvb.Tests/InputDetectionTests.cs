using Nightwave.Core.Storage;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using Nightwave.Tuners.Dvb;
using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.Mpeg;
using Nightwave.Tuners.Dvb.Scanning;
using Nightwave.Tuners.Dvb.Streaming;
using Nightwave.Tuners.Dvb.Transport;
using Nightwave.Tuners.Dvb.Tuning;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>
/// The tuner's signal report, the loss count of the stream, and what they are used for: telling
/// within a lock window that a transponder does not lock, and finding which satellite is on
/// which input of a SAT>IP server.
/// </summary>
public sealed class InputDetectionTests : IDisposable
{
    /// <summary>What a Kathrein EXIP-418 sends while tuned to 11836 MHz H on Astra 19.2E.</summary>
    private const string KathreinReport = "ver=1.0;src=1;tuner=2,224,1,15,11836.00,h,dvbs,qpsk,off,0.35,27500,34;pids=0,16,17";

    private static readonly ScanTimings Timings = new()
    {
        Lock = TimeSpan.FromMilliseconds(150),
        BlindLock = TimeSpan.FromMilliseconds(100),
        Pat = TimeSpan.FromMilliseconds(400),
        ProgramMaps = TimeSpan.FromSeconds(1),
        Sdt = TimeSpan.FromSeconds(1),
        Nit = TimeSpan.FromSeconds(1),
        Transponder = TimeSpan.FromSeconds(3),
    };

    private static readonly TuningParameters Astra = SampleMux.Tuning;

    private static readonly TuningParameters HotBird = new()
    {
        System = DvbSystem.DvbS,
        FrequencyKhz = 11034000,
        Polarization = Polarization.Vertical,
        SymbolRate = 27500,
        Modulation = "qpsk",
        Fec = "34",
    };

    private readonly string _directory = Directory.CreateTempSubdirectory("nightwave-satip-inputs-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void The_kathrein_report_gives_level_lock_and_quality()
    {
        var signal = TunerSignal.Parse(KathreinReport);

        Assert.NotNull(signal);
        Assert.Equal(224, signal.Level);
        Assert.True(signal.Locked);
        Assert.Equal(15, signal.Quality);
        Assert.Equal(1, signal.Source);
        Assert.Equal(2, signal.FrontEnd);
        Assert.Equal("lock, level 224, quality 15", signal.Describe());
    }

    [Fact]
    public void Reports_of_cable_and_unlocked_tuners_are_read_and_anything_else_is_not()
    {
        var cable = TunerSignal.Parse("ver=1.2;tuner=1,0,0,0,346.00,8,dvbc,256qam,6900,,,,0;pids=0");

        Assert.NotNull(cable);
        Assert.False(cable.Locked);
        Assert.Equal(0, cable.Level);
        Assert.Null(cable.Source);
        Assert.Equal("no lock (level 0, quality 0)", cable.Describe());

        Assert.Null(TunerSignal.Parse("ver=1.0;src=1;pids=0,16"));
        Assert.Null(TunerSignal.Parse("ver=1.0;tuner=1,high,1,15"));
        Assert.Null(TunerSignal.Parse(""));
    }

    [Fact]
    public void The_report_is_found_in_a_compound_rtcp_packet()
    {
        // Sender report, source description, then the APP packet SAT>IP puts its report in.
        byte[] senderReport = [0x80, 200, 0, 6, .. new byte[24]];
        byte[] description = [0x81, 202, 0, 2, 0, 0, 0, 1, 1, 0, 0, 0];

        var datagram = senderReport.Concat(description).Concat(App(KathreinReport)).ToArray();

        Assert.True(TunerSignal.TryReadRtcp(datagram, out var signal));
        Assert.Equal(224, signal.Level);
        Assert.True(signal.Locked);

        Assert.False(TunerSignal.TryReadRtcp(senderReport, out _));
        Assert.False(TunerSignal.TryReadRtcp([0x80, 204, 0, 100], out _));
        Assert.False(TunerSignal.TryReadRtcp(new byte[3], out _));
    }

    [Fact]
    public void Lost_datagrams_are_counted_across_the_wrap_of_the_sequence_number()
    {
        var tracker = new RtpSequenceTracker();

        foreach (var sequence in new[] { 65533, 65534, 65535, 0, 1 })
        {
            Assert.Equal(0, tracker.Take(sequence));
        }

        // 2 and 3 went missing; 65535 to 2 over the wrap is two more.
        Assert.Equal(2, tracker.Take(4));
        Assert.Equal(0, tracker.Take(5));

        var wrapping = new RtpSequenceTracker();
        wrapping.Take(65535);
        Assert.Equal(2, wrapping.Take(2));

        Assert.Equal(4, tracker.Lost + wrapping.Lost);
        Assert.Equal(7, tracker.Received);
    }

    [Fact]
    public void A_late_or_repeated_datagram_is_not_a_loss()
    {
        var tracker = new RtpSequenceTracker();

        tracker.Take(100);
        Assert.Equal(1, tracker.Take(102));

        // 101 arrives late, 102 twice: neither is lost, and the count does not jump by 65000.
        Assert.Equal(0, tracker.Take(101));
        Assert.Equal(0, tracker.Take(102));
        Assert.Equal(0, tracker.Take(103));

        Assert.Equal(1, tracker.Lost);
        Assert.Equal(5, tracker.Received);
    }

    [Fact]
    public void The_udp_receive_buffer_is_as_large_as_the_system_allows()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var before = socket.ReceiveBufferSize;

        var granted = SatIpRtspTransport.GrowReceiveBuffer(socket);

        Assert.True(granted >= before, $"{granted} bytes granted, {before} before.");
        Assert.Equal(socket.ReceiveBufferSize, granted);
    }

    [Fact]
    public async Task A_transponder_without_lock_fails_after_the_lock_window_with_the_level()
    {
        var transport = new FakeTransport(new TuneRequest(HotBird, 1), [])
        {
            Silent = true,
            Status = new TransportStatus { Signal = new TunerSignal { Level = 0, Locked = false, Quality = 0 }, SignalReports = 3 },
        };

        var clock = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<TunerTransportException>(() =>
            MuxScanner.ScanAsync(transport, Timings with { Pat = TimeSpan.FromSeconds(10) }, readNetwork: true, CancellationToken.None));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"Took {clock.Elapsed}.");
        Assert.StartsWith("no lock (level 0, quality 0)", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, error.Signal!.Level);
    }

    [Fact]
    public async Task Data_wins_over_a_no_lock_report_that_lags_behind()
    {
        // A Kathrein EXIP-418 reports level 0 and no lock for a while after the first packets
        // arrive, and a PAT can take longer than the lock window after a band switch. The scan
        // lost transponders to that; packets arriving mean the tuner has locked.
        var inner = new FakeTransport(new TuneRequest(Astra, 1), SampleMux.Packets());
        var transport = new LateTables(inner, TimeSpan.FromMilliseconds(600))
        {
            Status = new TransportStatus { Signal = new TunerSignal { Level = 0, Locked = false, Quality = 0 }, SignalReports = 3 },
        };

        var result = await MuxScanner.ScanAsync(transport, Timings with { Pat = TimeSpan.FromSeconds(5) }, readNetwork: false, CancellationToken.None);

        Assert.NotEmpty(result.Services);
    }

    [Fact]
    public async Task A_transponder_that_locks_but_carries_nothing_says_so()
    {
        var transport = new FakeTransport(new TuneRequest(HotBird, 1), [])
        {
            Silent = true,
            Status = new TransportStatus { Signal = new TunerSignal { Level = 180, Locked = true, Quality = 12 }, SignalReports = 3 },
        };

        var error = await Assert.ThrowsAsync<TunerTransportException>(() =>
            MuxScanner.ScanAsync(transport, Timings, readNetwork: true, CancellationToken.None));

        Assert.Contains("reports lock, level 180, quality 12, but no program association table arrived", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Each_input_is_detected_from_the_nit_of_the_first_transponder_that_locks()
    {
        var transports = new FakeTransportFactory { ReportSignal = true }
            .Add(Astra, SampleMux.Packets(), source: 1)
            .Add(HotBird, SatellitePackets(HotBird, 130), source: 2);

        var (detector, cache, device) = Create(transports);

        var detected = await detector.DetectAsync(device, CancellationToken.None);

        var inputs = detected.Inputs.OrderBy(input => input.Source).ToList();
        Assert.Equal([192, 130, null, null], inputs.Select(input => input.Position));
        Assert.Equal("nit", inputs[0].Evidence);
        Assert.True(inputs[1].LockedOn!.IsSameTransponder(HotBird));

        // Input 1 on the first try; input 2 after 19.2E did not lock; inputs 3 and 4 after the
        // first transponder of each position reported no signal at all.
        Assert.Equal(1, Probes(transports, 1));
        Assert.Equal(2, Probes(transports, 2));
        Assert.Equal(InitialTransponders.Positions.Count, Probes(transports, 3));
        Assert.Equal(InitialTransponders.Positions.Count, Probes(transports, 4));
        Assert.Contains("no signal at all", inputs[2].Failure, StringComparison.Ordinal);

        // Kept, and handed to the devices.
        var kept = await cache.GetAsync(device.Id, CancellationToken.None);
        Assert.Equal(
            [(1, 192), (2, 130)],
            DeviceDirectory.WithInputs(device, kept).SatelliteInputs.Select(input => (input.Source, input.Position.Tenths)));
        Assert.False(detector.IsDue(kept));
    }

    [Fact]
    public async Task The_network_says_where_the_dish_points_even_a_tenth_off_the_table()
    {
        var transports = new FakeTransportFactory { ReportSignal = true }.Add(Astra, SampleMux.Packets(orbital: 193), source: 1);
        var (detector, _, device) = Create(transports);

        var fresh = await detector.DetectInputAsync(device, 1, guess: null, tuning: null, CancellationToken.None);
        Assert.Equal(193, fresh.Position);

        // What an earlier version had typed in is kept where the network agrees within a tenth:
        // the channels found with it carry it in their ids.
        var guessed = await detector.DetectInputAsync(device, 1, new OrbitalPosition(192), tuning: null, CancellationToken.None);
        Assert.Equal(192, guessed.Position);
    }

    [Fact]
    public async Task A_guess_is_tried_first_and_a_lock_without_a_nit_falls_back_to_the_table()
    {
        // Hot Bird locks, but its transponder sends no NIT.
        var withoutNit = TsBuilder.Packets(TsBuilder.Pat(1, 0, (100, 0x100)), 0);
        var transports = new FakeTransportFactory { ReportSignal = true }.Add(HotBird, Enumerable.Repeat(withoutNit, 20).SelectMany(packets => packets), source: 1);
        var (detector, _, device) = Create(transports);

        var detected = await detector.DetectInputAsync(device, 1, new OrbitalPosition(130), tuning: null, CancellationToken.None);

        Assert.Equal(130, detected.Position);
        Assert.Equal("lock", detected.Evidence);
        Assert.True(transports.Created[0].Request.Tuning.IsSameTransponder(HotBird));
    }

    private static (InputDetector Detector, InputCache Cache, TunerDevice Device) Create(FakeTransportFactory transports)
    {
        var cache = new InputCache(null);
        var pool = new TunerPool(transports, new FakeDescramblerFactory(), new DescramblerSlots(), new FeedTimings(), NullLoggerFactory.Instance);
        var detector = new InputDetector(cache, pool, transports, Timings, TimeProvider.System, NullLogger<InputDetector>.Instance);

        return (detector, cache, SampleMux.Device() with { SatelliteInputs = [] });
    }

    private static int Probes(FakeTransportFactory transports, int source) =>
        transports.Created.Count(transport => transport.Request.Source == source);

    /// <summary>A transponder with a PAT and a NIT that puts it at <paramref name="orbital"/>.</summary>
    private static List<byte[]> SatellitePackets(TuningParameters tuning, int orbital)
    {
        var pat = TsBuilder.Pat(7, 0, (0, 0x10), (70, 0x100));
        var nit = TsBuilder.Nit(
            318,
            0,
            [],
            (7, 318, TsBuilder.SatelliteDelivery(tuning.FrequencyKhz / 1000m, orbital, tuning.Polarization == Polarization.Horizontal, false, tuning.SymbolRate ?? 27500, 3)));

        var packets = new List<byte[]>();
        for (var round = 0; round < 4; round++)
        {
            packets.AddRange(TsBuilder.Packets(pat, 0, round));
            packets.AddRange(TsBuilder.Packets(nit, 0x10, round));
        }

        return packets;
    }

    /// <summary>A SAT>IP APP packet around a report string.</summary>
    private static byte[] App(string report)
    {
        var text = Encoding.ASCII.GetBytes(report);
        var padded = (text.Length + 3) / 4 * 4;
        var packet = new byte[16 + padded];

        packet[0] = 0x80;
        packet[1] = 204;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)((packet.Length / 4) - 1));
        "SES1"u8.CopyTo(packet.AsSpan(8));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(14), (ushort)text.Length);
        text.CopyTo(packet.AsSpan(16));

        return packet;
    }
}

/// <summary>
/// A tuner whose tables come late: stuffing packets first, for as long as <c>delay</c>, then the
/// transponder. What it reports about its signal is whatever the test says, all the while.
/// </summary>
internal sealed class LateTables(FakeTransport inner, TimeSpan delay) : ITsTransport
{
    private readonly Stopwatch _clock = new();

    public TuneRequest Request => inner.Request;

    public TransportStatus Status { get; set; } = TransportStatus.Unknown;

    public Task StartAsync(IReadOnlyCollection<int> pids, CancellationToken cancellationToken)
    {
        _clock.Start();
        return inner.StartAsync(pids, cancellationToken);
    }

    public Task SetPidsAsync(IReadOnlyCollection<int> pids, CancellationToken cancellationToken) =>
        inner.SetPidsAsync(pids, cancellationToken);

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_clock.Elapsed >= delay)
        {
            return await inner.ReadAsync(buffer, cancellationToken);
        }

        await Task.Delay(5, cancellationToken);

        // One null packet: sync byte, pid 0x1FFF, payload only.
        var stuffing = new byte[TransportPacket.Size];
        stuffing[0] = 0x47;
        stuffing[1] = 0x1F;
        stuffing[2] = 0xFF;
        stuffing[3] = 0x10;
        stuffing.CopyTo(buffer);

        return stuffing.Length;
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
