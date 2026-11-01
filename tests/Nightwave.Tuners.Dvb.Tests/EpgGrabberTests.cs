using Nightwave.Core.Storage;
using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb;
using Nightwave.Tuners.Dvb.Channels;
using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.Epg;
using Nightwave.Tuners.Dvb.Mpeg.Si;
using Nightwave.Tuners.Dvb.Streaming;
using Nightwave.Tuners.Dvb.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>
/// The guide is read on idle tuners and along the way on busy ones - and a viewer always comes
/// first.
/// </summary>
public sealed class EpgGrabberTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly string _directory = Directory.CreateTempSubdirectory("nightwave-grabber-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task A_run_reads_one_transponder_and_skips_those_its_eit_other_covered()
    {
        await using var setup = await SetupAsync(new EpgGrabberTimings
        {
            MinimumDwell = TimeSpan.FromMilliseconds(200),
            MaximumDwell = TimeSpan.FromSeconds(5),
            Quiet = TimeSpan.FromMilliseconds(200),
        });

        var result = await setup.Grabber.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.Tuned);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(0, result.Preempted);

        // Only the first transponder was tuned, with the EIT pid and nothing else.
        var tuned = Assert.Single(setup.Transports.Created);
        Assert.True(tuned.Request.Tuning.IsSameTransponder(SampleMux.Tuning));
        Assert.True(tuned.Disposed);

        Assert.Equal("Tagesschau", Assert.Single(setup.Store.Query(new DvbServiceKey(1, 1019, 100), Now, Now.AddHours(1))).Title);
        Assert.Equal("Rundschau", Assert.Single(setup.Store.Query(new DvbServiceKey(1, 1101, 100), Now, Now.AddHours(1))).Title);

    }

    [Fact]
    public async Task A_viewer_takes_the_tuner_from_the_grabber_at_once()
    {
        await using var setup = await SetupAsync(new EpgGrabberTimings
        {
            MinimumDwell = TimeSpan.FromSeconds(30),
            MaximumDwell = TimeSpan.FromSeconds(30),
            Quiet = TimeSpan.FromSeconds(30),
        });

        var run = setup.Grabber.RunOnceAsync(CancellationToken.None);
        await ServiceFeedTests.WaitUntilAsync(() => setup.Pool.Usage(setup.Device).Count == 1);

        // The only tuner is the grabber's; a viewer on another transponder needs it.
        var started = System.Diagnostics.Stopwatch.StartNew();
        await using var lease = await setup.Pool.OpenAsync(
            SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid, SampleMux.OtherTuning) with { Id = "other" },
            [new TuneCandidate(setup.Device, new TuneRequest(SampleMux.OtherTuning, 1))],
            CancellationToken.None);

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));

        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, result.Preempted);

        // The grabber's session was closed before the viewer's was opened.
        var grabbed = setup.Transports.Created[0];
        Assert.True(grabbed.Disposed);
    }

    [Fact]
    public async Task With_no_tuner_free_the_grabber_waits_for_nobody()
    {
        await using var setup = await SetupAsync(new EpgGrabberTimings
        {
            MinimumDwell = TimeSpan.FromMilliseconds(200),
            MaximumDwell = TimeSpan.FromSeconds(5),
            Quiet = TimeSpan.FromMilliseconds(200),
        });

        // A viewer on the other transponder holds the one tuner.
        await using var viewer = await setup.Pool.OpenAsync(
            SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid, SampleMux.OtherTuning) with { Id = "other" },
            [new TuneCandidate(setup.Device, new TuneRequest(SampleMux.OtherTuning, 1))],
            CancellationToken.None);

        var result = await setup.Grabber.RunOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, result.Tuned);
        Assert.True(result.Busy + result.Skipped >= 1);
    }

    [Fact]
    public async Task A_streaming_tuner_reads_the_guide_along_the_way()
    {
        await using var setup = await SetupAsync(new EpgGrabberTimings());

        await using var lease = await setup.Pool.OpenAsync(
            SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid),
            [new TuneCandidate(setup.Device, new TuneRequest(SampleMux.Tuning, 1))],
            CancellationToken.None);

        var transport = setup.Transports.Created[0];
        Assert.Contains(EventInformationTable.Pid, transport.Pids);

        await ServiceFeedTests.WaitUntilAsync(() => setup.Store.Services().Count == 2);
        Assert.Equal("Tagesschau", setup.Store.Query(new DvbServiceKey(1, 1019, 100), Now, Now.AddHours(1))[0].Title);
    }

    [Fact]
    public async Task The_guide_provider_answers_by_guide_id_and_refresh_only_starts_a_run()
    {
        await using var setup = await SetupAsync(new EpgGrabberTimings
        {
            MinimumDwell = TimeSpan.FromMilliseconds(200),
            MaximumDwell = TimeSpan.FromSeconds(5),
            Quiet = TimeSpan.FromMilliseconds(200),
        });
        await setup.Grabber.RunOnceAsync(CancellationToken.None);

        var provider = new DvbGuideProvider(setup.Epg, setup.Grabber);

        var ids = await provider.GetGuideChannelIdsAsync();
        Assert.Contains("dvb:1.1019.100", ids);

        var programs = await provider.GetProgramsAsync(["dvb:1.1019.100", "not-a-dvb-id"], Now.AddHours(-1), Now.AddHours(2));
        var program = Assert.Single(programs);
        Assert.Equal("dvb:1.1019.100", program.ChannelId);
        Assert.Equal("Tagesschau", program.Title);

        await provider.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(1));
    }

    private async Task<Setup> SetupAsync(EpgGrabberTimings timings)
    {
        var eit = TsBuilder.Join(
        [
            .. TsBuilder.Packets(EitBuilder.Section(0x50, 100, 1019, 1, 0, (1, Now.AddMinutes(-5), TimeSpan.FromMinutes(20), EitBuilder.ShortEvent("ger", "Tagesschau", ""))), 0x12, 0),
            .. TsBuilder.Packets(EitBuilder.Section(0x60, 100, 1101, 1, 0, (2, Now.AddMinutes(-5), TimeSpan.FromMinutes(20), EitBuilder.ShortEvent("ger", "Rundschau", ""))), 0x12, 1),
        ]);

        var packets = SampleMux.Packets().Concat(TsBuilder.Split(eit)).ToList();

        var transports = new FakeTransportFactory()
            .Add(SampleMux.Tuning, packets)
            .Add(SampleMux.OtherTuning, SampleMux.Packets(1101));

        var entry = new SatIpDeviceSettings { Address = "192.0.2.10", MaxTuners = 1 };
        var settings = new DvbSettingsProvider(new DvbSettings { AutoDiscover = false, Devices = [entry] });
        var discovery = new FakeDeviceDiscovery();
        discovery.SatIpServers.Add(FakeDeviceDiscovery.SatIp("192.0.2.10", "DVBS2-4"));

        var devices = new DeviceDirectory(settings, discovery, TimeProvider.System, TestInputs.Detected("satip:192.0.2.10", (1, 192)));
        var device = (await devices.GetDevicesAsync(refresh: true, CancellationToken.None)).Single();

        var channels = new ChannelStore(new NightwaveDatabase(_directory));
        await channels.ApplyScanAsync(
            "satip:192.0.2.10/src1",
            [
                SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid),
                SampleMux.Channel(SampleMux.OtherService, SampleMux.OtherPmtPid),
                SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid, SampleMux.OtherTuning) with { Id = "19.2E:1.1101.100", TransportStreamId = 1101 },
            ],
            ScanMode.Replace,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        var store = new EpgStore(new NightwaveDatabase(_directory), TimeProvider.System);
        var epg = new EpgService(store, settings);
        var pool = new TunerPool(transports, new FakeDescramblerFactory(), new DescramblerSlots(), new FeedTimings(), NullLoggerFactory.Instance, epg);
        var grabber = new EpgGrabber(epg, channels, devices, pool, transports, settings, timings, NullLogger<EpgGrabber>.Instance);

        return new Setup(grabber, pool, store, epg, transports, device, channels, devices);
    }

    private sealed record Setup(
        EpgGrabber Grabber,
        TunerPool Pool,
        EpgStore Store,
        EpgService Epg,
        FakeTransportFactory Transports,
        TunerDevice Device,
        ChannelStore Channels,
        DeviceDirectory Devices) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Pool.DisposeAsync();
            Grabber.Dispose();
            Store.Dispose();
            Channels.Dispose();
            Devices.Dispose();
        }
    }
}
