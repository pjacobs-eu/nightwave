using Nightwave.Core.Storage;
using System.Text.Json.Nodes;
using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb;
using Nightwave.Tuners.Dvb.Channels;
using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.Scanning;
using Nightwave.Tuners.Dvb.Streaming;
using Nightwave.Tuners.Dvb.Tuning;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>
/// A scan against transponders played from memory: it starts from the built-in table, learns
/// the rest from the NIT, keeps what it finds, and gives every channel the same id the next
/// time.
/// </summary>
public sealed class ChannelScanTests : IDisposable
{
    private static readonly ScanTimings Timings = new()
    {
        Lock = TimeSpan.FromMilliseconds(200),
        BlindLock = TimeSpan.FromMilliseconds(100),
        Pat = TimeSpan.FromMilliseconds(500),
        ProgramMaps = TimeSpan.FromSeconds(1),
        Sdt = TimeSpan.FromSeconds(1),
        Nit = TimeSpan.FromSeconds(3),
        Transponder = TimeSpan.FromSeconds(6),
    };

    /// <summary>A transponder the built-in table does not know, only the NIT.</summary>
    private static readonly TuningParameters Learned = SampleMux.Tuning with { FrequencyKhz = 12603000, Fec = "34", RollOff = "0.35" };

    private readonly string _directory = Directory.CreateTempSubdirectory("nightwave-satip-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task A_satellite_scan_learns_transponders_from_the_nit_and_keeps_what_it_finds()
    {
        var setup = Setup(Network());
        var progress = new RecordingProgress();

        var result = await setup.Scanner.ScanAsync(new ScanRequest { TargetId = "satip:192.0.2.10/src1" }, progress);

        Assert.Equal(4, result.TelevisionFound);
        Assert.Equal(2, result.RadioFound);
        Assert.Equal(2, result.EncryptedFound);
        Assert.Equal(0, result.Removed);

        // The other two start transponders did not lock; the scan went on and says so.
        Assert.Equal(2, result.Failures.Count);
        Assert.Contains(result.Failures, failure => failure.StartsWith("11494 MHz H 22000 DVB-S2", StringComparison.Ordinal));

        // It tuned the transponder it only knew from the NIT.
        Assert.Contains(setup.Transports.Created, transport => transport.Request.Tuning.IsSameTransponder(Learned) && transport.Started);

        Assert.NotEmpty(progress.Reports);
        Assert.Equal(1, progress.Reports[^1].Fraction);
        Assert.Contains(progress.Reports, report => report.Current == "11836 MHz H 27500 DVB-S");
        Assert.Equal(4, progress.Reports[^1].TransponderCount);

        var channels = await setup.Store.GetChannelsAsync(CancellationToken.None);
        var erste = channels.Single(channel => channel.Id == "19.2E:1.1019.100");

        Assert.Equal("Das Erste HD", erste.Name);
        Assert.Equal("ARD", erste.Provider);
        Assert.Equal(ChannelKind.Television, erste.Kind);
        Assert.False(erste.Scrambled);
        Assert.Equal(192, erste.OrbitalPosition);
        Assert.Equal(SampleMux.ClearPmtPid, erste.PmtPid);
        Assert.Equal(SampleMux.ClearVideoPid, erste.PcrPid);
        Assert.Equal(new[] { SampleMux.ClearVideoPid, SampleMux.ClearAudioPid }, erste.Pids);
        Assert.True(erste.Tuning!.IsSameTransponder(SampleMux.Tuning));
        Assert.Equal(new[] { "satip:192.0.2.10/src1" }, erste.FoundBy);

        var sky = channels.Single(channel => channel.Id == "19.2E:1.1019.300");
        Assert.True(sky.Scrambled);
        Assert.Equal(new[] { 0x1830 }, sky.CaSystems);

        Assert.Equal(ChannelKind.Radio, channels.Single(channel => channel.Id == "19.2E:1.1019.200").Kind);
        Assert.Contains(channels, channel => channel.Id == "19.2E:1.1101.100");

        // Written down: a store that starts afresh has them.
        using var again = new ChannelStore(new NightwaveDatabase(_directory));
        Assert.Equal(channels.Count, (await again.GetChannelsAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Channel_ids_stay_the_same_across_scans_and_a_replacing_scan_removes_what_is_gone()
    {
        var first = Setup(Network());
        await first.Scanner.ScanAsync(new ScanRequest { TargetId = "satip:192.0.2.10/src1" }, new RecordingProgress());
        var before = (await first.Store.GetChannelsAsync(CancellationToken.None)).Select(channel => channel.Id).Order().ToList();

        // Again, on a fresh store over the same file, with the learned transponder gone quiet.
        var second = Setup(new FakeTransportFactory().Add(SampleMux.Tuning, NetworkPackets(SampleMux.TransportStreamId)));
        var result = await second.Scanner.ScanAsync(new ScanRequest { TargetId = "satip:192.0.2.10/src1" }, new RecordingProgress());
        var after = (await second.Store.GetChannelsAsync(CancellationToken.None)).Select(channel => channel.Id).Order().ToList();

        Assert.Equal(3, result.Removed);
        Assert.Equal(before.Where(id => id.Contains(".1019.", StringComparison.Ordinal)), after);
    }

    [Fact]
    public async Task A_merging_scan_removes_nothing()
    {
        var first = Setup(Network());
        await first.Scanner.ScanAsync(new ScanRequest { TargetId = "satip:192.0.2.10/src1" }, new RecordingProgress());

        var second = Setup(new FakeTransportFactory().Add(SampleMux.Tuning, NetworkPackets(SampleMux.TransportStreamId)));
        var result = await second.Scanner.ScanAsync(
            new ScanRequest { TargetId = "satip:192.0.2.10/src1", Mode = ScanMode.Merge },
            new RecordingProgress());

        Assert.Equal(0, result.Removed);
        Assert.Equal(6, (await second.Store.GetChannelsAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Radio_and_scrambled_channels_are_left_out_when_asked()
    {
        var setup = Setup(Network());

        var result = await setup.Scanner.ScanAsync(
            new ScanRequest { TargetId = "satip:192.0.2.10/src1", IncludeRadio = false, IncludeEncrypted = false },
            new RecordingProgress());

        Assert.Equal(2, result.TelevisionFound);
        Assert.Equal(0, result.RadioFound);
        Assert.Equal(0, result.EncryptedFound);
        Assert.All(await setup.Store.GetChannelsAsync(CancellationToken.None), channel =>
        {
            Assert.Equal(ChannelKind.Television, channel.Kind);
            Assert.False(channel.Scrambled);
        });
    }

    [Fact]
    public async Task Targets_are_every_detected_input_and_say_what_they_hold()
    {
        var setup = Setup(Network(), inputs: [(1, 192), (2, 130), (3, 192)]);

        await setup.Scanner.ScanAsync(new ScanRequest { TargetId = "satip:192.0.2.10/src1" }, new RecordingProgress());
        var targets = await setup.Scanner.GetTargetsAsync();

        var astra = targets.Single(target => target.Id == "satip:192.0.2.10/src1");
        Assert.Equal("Test SAT>IP (192.0.2.10) - 19.2°E (src=1)", astra.Name);
        Assert.Equal(DeliverySystem.Satellite, astra.DeliverySystem);
        Assert.Equal(6, astra.KnownChannels);
        Assert.NotNull(astra.LastScannedAt);
        Assert.Null(astra.Error);

        var hotBird = targets.Single(target => target.Id == "satip:192.0.2.10/src2");
        Assert.Equal("Test SAT>IP (192.0.2.10) - 13.0°E (src=2)", hotBird.Name);
        Assert.Equal(0, hotBird.KnownChannels);
        Assert.Null(hotBird.LastScannedAt);

        // Input 3 sees the same satellite as input 1: the same channels, so no target of its own.
        Assert.DoesNotContain(targets, target => target.Id == "satip:192.0.2.10/src3");
        Assert.Equal(2, targets.Count);
    }

    [Fact]
    public async Task A_device_on_which_nothing_is_detected_cannot_be_scanned_and_says_why()
    {
        var setup = Setup(new FakeTransportFactory { ReportSignal = true }, inputs: []);

        var target = Assert.Single(await setup.Scanner.GetTargetsAsync());
        Assert.Equal("satip:192.0.2.10/satellite", target.Id);
        Assert.Contains("No satellite was found on any input of Test SAT>IP (192.0.2.10)", target.Error, StringComparison.Ordinal);
        Assert.Contains("input 1: no signal at all", target.Error, StringComparison.Ordinal);

        var error = await Assert.ThrowsAsync<TunerException>(() =>
            setup.Scanner.ScanAsync(new ScanRequest { TargetId = target.Id }, new RecordingProgress()));
        Assert.Equal("scan.target_unusable", error.Code);
    }

    [Fact]
    public async Task A_dish_that_was_turned_is_scanned_where_it_points_now()
    {
        // Detected on 13.0E once; the network on the input now says 19.2E.
        var setup = Setup(Network(), inputs: [(1, 130)]);

        var result = await setup.Scanner.ScanAsync(new ScanRequest { TargetId = "satip:192.0.2.10/src1" }, new RecordingProgress());

        Assert.Equal(4, result.TelevisionFound);
        Assert.All(await setup.Store.GetChannelsAsync(CancellationToken.None), channel => Assert.StartsWith("19.2E:", channel.Id, StringComparison.Ordinal));

        var device = Assert.Single(await setup.Devices.GetDevicesAsync(refresh: false, CancellationToken.None));
        Assert.Equal(192, Assert.Single(device.SatelliteInputs).Position.Tenths);
    }

    [Fact]
    public void A_position_without_start_transponders_has_nothing_to_start_from_and_a_tenth_off_is_the_same_satellite()
    {
        ScanInput Input(int tenths) => new()
        {
            TargetId = "satip:192.0.2.10/src1",
            Device = SampleMux.Device(),
            Delivery = DeliverySystem.Satellite,
            Source = 1,
            Position = new OrbitalPosition(tenths),
        };

        Assert.Empty(Input(420).StartTransponders());
        Assert.Equal(3, Input(193).StartTransponders().Count);
        Assert.True(Input(193).StartTransponders()[0][0].IsSameTransponder(SampleMux.Tuning));
    }

    [Fact]
    public async Task An_unknown_target_is_an_error_not_an_empty_scan()
    {
        var setup = Setup(Network());

        var error = await Assert.ThrowsAsync<TunerException>(() =>
            setup.Scanner.ScanAsync(new ScanRequest { TargetId = "satip:203.0.113.1/src1" }, new RecordingProgress()));

        Assert.Equal("scan.target_missing", error.Code);
    }

    [Fact]
    public async Task A_cable_scan_searches_until_a_transponder_locks_and_then_follows_the_nit()
    {
        var start = new TuningParameters { System = DvbSystem.DvbC, FrequencyKhz = 346000, SymbolRate = 6900, Modulation = "256qam" };
        var next = start with { FrequencyKhz = 354000 };

        var cableNit = (int tsid) => CablePackets(tsid, [(1, 346m), (2, 354m)]);
        var transports = new FakeTransportFactory { ReportSignal = true, Exact = true }.Add(start, cableNit(1)).Add(next, cableNit(2));

        var setup = Setup(transports, capabilities: "DVBC-1", inputs: []);
        var progress = new RecordingProgress();

        var targets = await setup.Scanner.GetTargetsAsync();
        Assert.Equal("satip:192.0.2.10/cable", Assert.Single(targets).Id);

        var result = await setup.Scanner.ScanAsync(new ScanRequest { TargetId = "satip:192.0.2.10/cable" }, progress);

        Assert.Empty(result.Failures);
        Assert.Equal(2, result.TelevisionFound);

        // 306 to 338 had no carrier, 346 locked with its NIT, 354 came from the NIT: the search stopped at 346.
        Assert.Equal(
            [306000, 314000, 322000, 330000, 338000, 346000, 354000],
            setup.Transports.Created.Select(transport => transport.Request.Tuning.FrequencyKhz));
        Assert.Contains(progress.Reports, report => report.Current == "338 MHz 6900 DVB-C");

        var channels = await setup.Store.GetChannelsAsync(CancellationToken.None);
        Assert.All(channels, channel =>
        {
            Assert.Equal(DeliverySystem.Cable, channel.Delivery);
            Assert.Equal("satip:192.0.2.10", channel.DeviceId);
            Assert.StartsWith("C@satip:192.0.2.10:", channel.Id, StringComparison.Ordinal);
        });
        Assert.Equal("7", channels.Single(channel => channel.TransportStreamId == 2).Number);
    }

    [Fact]
    public async Task A_cable_search_tries_the_settings_that_locked_last_first_and_every_setting_where_there_is_a_carrier()
    {
        var first = new TuningParameters { System = DvbSystem.DvbC, FrequencyKhz = 306000, SymbolRate = 6875, Modulation = "256qam" };
        var second = first with { FrequencyKhz = 314000 };

        // Two transponders without a NIT, so the search goes on; a carrier at 322 that locks with nothing.
        var transports = new FakeTransportFactory { ReportSignal = true, Exact = true }
            .Add(first, CablePackets(1, []))
            .Add(second, CablePackets(2, []))
            .AddCarrier(306000, 150)
            .AddCarrier(322000, 80);

        var setup = Setup(transports, capabilities: "DVBC-1", inputs: []);
        var result = await setup.Scanner.ScanAsync(new ScanRequest { TargetId = "satip:192.0.2.10/cable" }, new RecordingProgress());

        var tried = setup.Transports.Created.Select(transport => (transport.Request.Tuning.FrequencyKhz / 1000, transport.Request.Tuning.SymbolRate, transport.Request.Tuning.Modulation)).ToList();

        Assert.Equal(
            [
                (306, 6900, "256qam"),
                (306, 6875, "256qam"),
                (314, 6875, "256qam"),
                (322, 6875, "256qam"),
                (322, 6900, "256qam"),
                (322, 6900, "64qam"),
                (322, 6875, "64qam"),
                (330, 6875, "256qam"),
            ],
            tried.Take(8));

        // Everything else was tried once: no signal level, no other settings. One more try at 306
        // and three more at 322 than there are frequencies.
        Assert.Equal(BlindScan.CableFrequenciesKhz.Count + 4, tried.Count);
        Assert.Equal(2, result.TelevisionFound);
        Assert.Contains(result.Failures, failure => failure.Contains("none carried a network information table", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_terrestrial_search_reads_every_multiplex_that_locks_dvb_t2_first()
    {
        var t2 = new TuningParameters { System = DvbSystem.DvbT2, FrequencyKhz = 490000, Bandwidth = "8", Plp = 0 };
        var t = new TuningParameters { System = DvbSystem.DvbT, FrequencyKhz = 482000, Bandwidth = "8" };

        var transports = new FakeTransportFactory { ReportSignal = true, Exact = true }
            .Add(t, TerrestrialPackets(1))
            .Add(t2, TerrestrialPackets(2))
            .AddCarrier(482000, 90);

        var setup = Setup(transports, capabilities: "DVBT2-1", inputs: []);
        var result = await setup.Scanner.ScanAsync(new ScanRequest { TargetId = "satip:192.0.2.10/terrestrial" }, new RecordingProgress());

        var tried = setup.Transports.Created.Select(transport => transport.Request.Tuning.Describe()).ToList();

        // 474: no level on T2, so no T; 482: a carrier, T2 does not lock, T does; 490: T2.
        Assert.Equal(["474 MHz 8 MHz DVB-T2 PLP 0", "482 MHz 8 MHz DVB-T2 PLP 0", "482 MHz 8 MHz DVB-T", "490 MHz 8 MHz DVB-T2 PLP 0", "498 MHz 8 MHz DVB-T2 PLP 0"], tried.Take(5));
        Assert.Equal(BlindScan.TerrestrialFrequenciesKhz.Count + 1, tried.Count);
        Assert.Equal("690 MHz 8 MHz DVB-T2 PLP 0", tried[^1]);

        Assert.Equal(2, result.TelevisionFound);
        Assert.All(await setup.Store.GetChannelsAsync(CancellationToken.None), channel =>
        {
            Assert.Equal(DeliverySystem.Terrestrial, channel.Delivery);
            Assert.StartsWith("T@satip:192.0.2.10:", channel.Id, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task The_tuner_lists_what_the_scan_kept_and_refuses_what_it_cannot_open()
    {
        var setup = Setup(Network());
        await setup.Scanner.ScanAsync(new ScanRequest { TargetId = "satip:192.0.2.10/src1" }, new RecordingProgress());

        var descramblers = new FakeDescramblerFactory { IsConfigured = false };
        await using var pool = new TunerPool(setup.Transports, descramblers, new DescramblerSlots(), new FeedTimings(), NullLoggerFactory.Instance);
        var provider = new DvbTunerProvider(setup.Store, setup.Devices, pool, descramblers, NullLogger<DvbTunerProvider>.Instance);

        var channels = await provider.GetChannelsAsync();
        Assert.Equal(6, channels.Count);
        Assert.True(channels.Single(channel => channel.Id == "19.2E:1.1019.300").IsEncrypted);
        Assert.False(channels.Single(channel => channel.Id == "19.2E:1.1019.100").IsEncrypted);

        var scrambled = await Assert.ThrowsAsync<TunerException>(() =>
            provider.OpenAsync(new TunedStreamRequest { ChannelId = "19.2E:1.1019.300", ClientDeviceId = "test" }));
        Assert.Equal("tuner.descrambling_not_configured", scrambled.Code);
        Assert.Equal("Sky Cinema", scrambled.Values["channel"]);

        var missing = await Assert.ThrowsAsync<TunerException>(() =>
            provider.OpenAsync(new TunedStreamRequest { ChannelId = "19.2E:1.9999.1", ClientDeviceId = "test" }));
        Assert.Equal("tuner.channel_missing", missing.Code);

        await using var stream = await provider.OpenAsync(new TunedStreamRequest { ChannelId = "19.2E:1.1019.100", ClientDeviceId = "test" });
        Assert.Equal("19.2E:1.1019.100", stream.ChannelId);
        Assert.True(stream.IsActive);

        var tuners = await provider.GetTunersAsync();
        var tuner = Assert.Single(tuners);
        Assert.Equal(2, tuner.MaxStreams);
        Assert.Equal(1, tuner.ActiveStreams);
        Assert.Equal("19.2E:1.1019.100", tuner.ActiveChannelId);

        // A descrambling server opens the channel; it is still broadcast encrypted, and still shown so.
        descramblers.IsConfigured = true;
        Assert.True((await provider.GetChannelsAsync()).Single(channel => channel.Id == "19.2E:1.1019.300").IsEncrypted);
    }

    private (DvbChannelScanner Scanner, ChannelStore Store, DeviceDirectory Devices, FakeTransportFactory Transports) Setup(
        FakeTransportFactory transports,
        string capabilities = "DVBS2-2",
        (int Source, int Tenths)[]? inputs = null)
    {
        // Input 1 detected on 19.2E, unless a test says otherwise; none at all is an empty list.
        inputs ??= [(1, 192)];
        var entry = new SatIpDeviceSettings { Address = "192.0.2.10" };

        var discovery = new FakeDeviceDiscovery();
        var server = FakeDeviceDiscovery.SatIp("192.0.2.10", capabilities);
        discovery.SatIpServers.Add(server);

        var settings = new DvbSettingsProvider(new DvbSettings { AutoDiscover = false, Devices = [entry] });
        var cache = inputs.Length == 0 ? new InputCache(null) : TestInputs.Detected("satip:192.0.2.10", inputs);
        var devices = new DeviceDirectory(settings, discovery, TimeProvider.System, cache);
        var store = new ChannelStore(new NightwaveDatabase(_directory));
        var pool = new TunerPool(transports, new FakeDescramblerFactory(), new DescramblerSlots(), new FeedTimings(), NullLoggerFactory.Instance);
        var detector = new InputDetector(cache, pool, transports, Timings, TimeProvider.System, NullLogger<InputDetector>.Instance);

        var scanner = new DvbChannelScanner(
            devices,
            store,
            pool,
            transports,
            detector,
            Timings,
            TimeProvider.System,
            NullLogger<DvbChannelScanner>.Instance);

        return (scanner, store, devices, transports);
    }

    /// <summary>The start transponder and the one only its NIT knows.</summary>
    private static FakeTransportFactory Network() =>
        new FakeTransportFactory()
            .Add(SampleMux.Tuning, NetworkPackets(SampleMux.TransportStreamId))
            .Add(Learned, NetworkPackets(1101));

    private static List<byte[]> NetworkPackets(int tsid) =>
        SampleMux.Packets(tsid, [(SampleMux.TransportStreamId, SampleMux.Tuning), (1101, Learned)]);

    /// <summary>A terrestrial multiplex with one television service and no NIT.</summary>
    private static List<byte[]> TerrestrialPackets(int tsid)
    {
        const int Onid = 8468;
        var pat = TsBuilder.Pat(tsid, 0, (20 + tsid, 0x100));
        var pmt = TsBuilder.Pmt(20 + tsid, 0, 0x101, [], (0x24, 0x101, []), (0x0F, 0x102, []));
        var sdt = TsBuilder.Sdt(tsid, Onid, 0, (20 + tsid, false, TsBuilder.ServiceDescriptor(0x1F, "ARD", $"Antenne {tsid}")));

        var packets = new List<byte[]>();
        for (var round = 0; round < 4; round++)
        {
            packets.AddRange(TsBuilder.Packets(pat, 0, round));
            packets.AddRange(TsBuilder.Packets(pmt, 0x100, round));
            packets.AddRange(TsBuilder.Packets(sdt, 0x11, round));
            packets.Add(TsBuilder.EsPacket(0x101, round, 0xA1));
        }

        return packets;
    }

    /// <summary>A cable transponder with one television service, and a NIT with channel numbers.</summary>
    private static List<byte[]> CablePackets(int tsid, (int Tsid, decimal Mhz)[] network)
    {
        const int Onid = 61441;
        var pat = TsBuilder.Pat(tsid, 0, (10 + tsid, 0x100));
        var pmt = TsBuilder.Pmt(10 + tsid, 0, 0x101, [], (0x1B, 0x101, []), (0x0F, 0x102, []));
        var sdt = TsBuilder.Sdt(tsid, Onid, 0, (10 + tsid, false, TsBuilder.ServiceDescriptor(0x19, "Vodafone", $"Kabel {tsid}")));
        var nit = TsBuilder.Nit(
            Onid,
            0,
            [],
            [.. network.Select(entry => (entry.Tsid, Onid, (byte[])[.. TsBuilder.CableDelivery(entry.Mhz, 6900, 5), .. TsBuilder.Descriptor(0x83, 0, (byte)(10 + entry.Tsid), 0xFC, (byte)(5 + entry.Tsid))]))]);

        var packets = new List<byte[]>();
        for (var round = 0; round < 4; round++)
        {
            packets.AddRange(TsBuilder.Packets(pat, 0, round));
            packets.AddRange(TsBuilder.Packets(pmt, 0x100, round));
            packets.AddRange(TsBuilder.Packets(sdt, 0x11, round));
            packets.AddRange(TsBuilder.Packets(nit, 0x10, round));
            packets.Add(TsBuilder.EsPacket(0x101, round, 0xA1));
        }

        return packets;
    }
}
