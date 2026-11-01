using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb;
using Nightwave.Tuners.Dvb.Channels;
using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.Streaming;
using Nightwave.Tuners.Dvb.Transport;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>
/// Tuners are shared the way a receiver shares them: one per transponder, however many
/// channels and viewers are on it - and when there is none left, an error at once.
/// </summary>
public sealed class TunerPoolTests
{
    private static readonly FeedTimings Timings = new() { ProgramMap = TimeSpan.FromSeconds(5), Keys = TimeSpan.FromSeconds(5) };

    [Fact]
    public async Task Two_viewers_of_one_channel_share_one_tuner()
    {
        var transports = Transports();
        await using var pool = Pool(transports);
        var device = SampleMux.Device();
        var channel = SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid);

        var first = await pool.OpenAsync(channel, Candidates(device), CancellationToken.None);
        var second = await pool.OpenAsync(channel, Candidates(device), CancellationToken.None);

        Assert.Single(transports.Created);
        Assert.Same(first.Feed, second.Feed);

        await first.DisposeAsync();
        Assert.False(transports.Created[0].Disposed);

        await second.DisposeAsync();
        Assert.True(transports.Created[0].Disposed);
    }

    [Fact]
    public async Task Channels_on_one_transponder_share_a_tuner_by_adding_pids()
    {
        var transports = Transports();
        await using var pool = Pool(transports);
        var device = SampleMux.Device(tuners: 1);

        await using var first = await pool.OpenAsync(SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid), Candidates(device), CancellationToken.None);
        await using var second = await pool.OpenAsync(SampleMux.Channel(SampleMux.OtherService, SampleMux.OtherPmtPid), Candidates(device), CancellationToken.None);

        var transport = Assert.Single(transports.Created);
        Assert.NotSame(first.Feed, second.Feed);

        await ServiceFeedTests.WaitUntilAsync(() =>
            new[] { SampleMux.ClearPmtPid, SampleMux.ClearVideoPid, SampleMux.ClearAudioPid, SampleMux.OtherPmtPid, SampleMux.OtherVideoPid }
                .All(pid => transport.Pids.Contains(pid)));

        // Nothing of the scrambled service is asked for, nor its ECMs.
        Assert.DoesNotContain(SampleMux.ScrambledVideoPid, transport.Pids);
        Assert.DoesNotContain(SampleMux.EcmPid, transport.Pids);
    }

    [Fact]
    public async Task A_viewer_leaving_takes_their_pids_off_the_tuner()
    {
        var transports = Transports();
        await using var pool = Pool(transports);
        var device = SampleMux.Device();

        await using var first = await pool.OpenAsync(SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid), Candidates(device), CancellationToken.None);
        var second = await pool.OpenAsync(SampleMux.Channel(SampleMux.OtherService, SampleMux.OtherPmtPid), Candidates(device), CancellationToken.None);
        var transport = transports.Created[0];

        await ServiceFeedTests.WaitUntilAsync(() => transport.Pids.Contains(SampleMux.OtherVideoPid));
        await second.DisposeAsync();
        await ServiceFeedTests.WaitUntilAsync(() => !transport.Pids.Contains(SampleMux.OtherVideoPid));

        Assert.Contains(SampleMux.ClearVideoPid, transport.Pids);
        Assert.False(transport.Disposed);
    }

    [Fact]
    public async Task With_every_tuner_busy_the_answer_is_an_error_at_once_not_a_wait()
    {
        var transports = Transports();
        await using var pool = Pool(transports);
        var device = SampleMux.Device(tuners: 1);

        var busy = await pool.OpenAsync(SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid), Candidates(device), CancellationToken.None);
        var elsewhere = SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid, SampleMux.OtherTuning, name: "RTL") with { Id = "19.2E:1.1089.100" };

        var started = System.Diagnostics.Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<TunerException>(() => pool.OpenAsync(elsewhere, Candidates(device, SampleMux.OtherTuning), CancellationToken.None));

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(1));
        Assert.Equal(DvbProvider.Id, error.ProviderId);
        Assert.Equal("tuner.no_free_tuner", error.Code);
        Assert.Equal("RTL", error.Values["channel"]);
        Assert.Equal(1, error.Values["tuners"]);
        Assert.Contains("11836 MHz H 27500 DVB-S", error.Message, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(error.Remedy));

        // Once the first tuner is free, the other transponder can have it.
        await busy.DisposeAsync();
        await using var now = await pool.OpenAsync(elsewhere, Candidates(device, SampleMux.OtherTuning), CancellationToken.None);

        Assert.Equal(2, transports.Created.Count);
    }

    [Fact]
    public async Task A_second_device_on_the_same_dish_takes_over_when_the_first_is_busy()
    {
        var transports = Transports();
        await using var pool = Pool(transports);
        var first = SampleMux.Device(tuners: 1, host: "192.0.2.10");
        var second = SampleMux.Device(tuners: 1, host: "192.0.2.11");

        await using var one = await pool.OpenAsync(SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid), [.. Candidates(first), .. Candidates(second)], CancellationToken.None);
        var elsewhere = SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid, SampleMux.OtherTuning) with { Id = "19.2E:1.1089.100" };
        await using var two = await pool.OpenAsync(elsewhere, [.. Candidates(first, SampleMux.OtherTuning), .. Candidates(second, SampleMux.OtherTuning)], CancellationToken.None);

        Assert.Equal("satip:192.0.2.11", two.Multiplex.Device.Id);
    }

    [Fact]
    public async Task A_scan_holds_a_tuner_like_a_viewer_does()
    {
        await using var pool = Pool(Transports());
        var device = SampleMux.Device(tuners: 1);

        var scan = pool.ReserveForScan(device, DeliverySystem.Satellite, "input 1");

        var error = await Assert.ThrowsAsync<TunerException>(() =>
            pool.OpenAsync(SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid), Candidates(device), CancellationToken.None));
        Assert.Equal("tuner.no_free_tuner", error.Code);
        Assert.Contains("a channel scan", error.Message, StringComparison.Ordinal);

        await scan.DisposeAsync();
        await using var lease = await pool.OpenAsync(SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid), Candidates(device), CancellationToken.None);
    }

    [Fact]
    public async Task A_device_that_refuses_to_tune_gives_an_error_naming_it_and_frees_the_tuner()
    {
        var transports = new FakeTransportFactory();
        await using var pool = Pool(transports);
        var device = SampleMux.Device(tuners: 1);

        var error = await Assert.ThrowsAsync<TunerException>(() =>
            pool.OpenAsync(SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid, name: "Das Erste HD"), Candidates(device), CancellationToken.None));

        Assert.Equal("tuner.tune_failed", error.Code);
        Assert.Equal("Test SAT>IP (192.0.2.10)", error.Values["device"]);
        Assert.Contains("RTSP 408", error.Message, StringComparison.Ordinal);
        Assert.Empty(pool.Usage(device));
    }

    [Fact]
    public async Task Reading_a_shared_channel_gives_each_reader_the_whole_stream()
    {
        await using var pool = Pool(Transports());
        var device = SampleMux.Device();
        var channel = SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid);

        await using var lease = await pool.OpenAsync(channel, Candidates(device), CancellationToken.None);
        var stream = new DvbTunedStream(lease);

        await using var first = await stream.OpenReadAsync();
        await using var second = await stream.OpenReadAsync();

        var one = TsBuilder.Split(await ServiceFeedTests.ReadAvailableAsync(first));
        var two = TsBuilder.Split(await ServiceFeedTests.ReadAvailableAsync(second));

        Assert.Contains(one, packet => Nightwave.Tuners.Dvb.Mpeg.TransportPacket.Pid(packet) == SampleMux.ClearVideoPid);
        Assert.Contains(two, packet => Nightwave.Tuners.Dvb.Mpeg.TransportPacket.Pid(packet) == SampleMux.ClearVideoPid);
        Assert.True(stream.IsActive);
    }

    [Fact]
    public async Task The_descrambler_is_fed_batches_rather_than_single_datagrams()
    {
        var descramblers = new FakeDescramblerFactory();
        await using var pool = Pool(Transports(), descramblers);

        await using var lease = await pool.OpenAsync(
            SampleMux.Channel(SampleMux.ScrambledService, SampleMux.ScrambledPmtPid),
            Candidates(SampleMux.Device()),
            CancellationToken.None);

        var descrambler = Assert.Single(descramblers.Opened);
        await ServiceFeedTests.WaitUntilAsync(() => descrambler.LargestBatch >= 50);

        // The fake tuner delivers at most fourteen packets per read, as a SAT>IP server sends
        // seven per datagram; what the descrambler sees is many reads put together.
        Assert.True(descrambler.LargestBatch > 14);
    }

    [Fact]
    public async Task A_descrambling_problem_shows_on_the_tuner()
    {
        var descramblers = new FakeDescramblerFactory();
        await using var pool = Pool(Transports(), descramblers);
        var device = SampleMux.Device();

        await using var lease = await pool.OpenAsync(
            SampleMux.Channel(SampleMux.ScrambledService, SampleMux.ScrambledPmtPid, name: "Sky Cinema"),
            Candidates(device),
            CancellationToken.None);

        descramblers.Opened[0].Report(new TunerException("satip", "descrambler.no_control_word", "The descrambling server sent no control word.", "Check the card."));

        Assert.Equal("Sky Cinema: The descrambling server sent no control word.", Assert.Single(pool.Problems(device)));
    }

    [Fact]
    public async Task A_client_changing_channel_takes_over_their_tuner()
    {
        var transports = Transports();
        await using var pool = Pool(transports);
        var device = SampleMux.Device(tuners: 1);
        var channel = SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid);

        var old = await pool.OpenAsync(channel, Candidates(device), "tv", CancellationToken.None);
        await using var next = await pool.OpenAsync(Other(), Candidates(device, SampleMux.OtherTuning), "tv", CancellationToken.None);

        Assert.True(transports.Created[0].Disposed);
        Assert.Equal(2, transports.Created.Count);
        Assert.Equal("tuner.replaced_by_own_stream", Assert.IsType<TunerException>(old.Feed.Fault).Code);

        // The old player leaving afterwards costs the new one nothing.
        await old.DisposeAsync();
        Assert.False(transports.Created[1].Disposed);
    }

    [Fact]
    public async Task Another_clients_tuner_is_not_taken()
    {
        await using var pool = Pool(Transports());
        var device = SampleMux.Device(tuners: 1);

        await using var running = await pool.OpenAsync(SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid), Candidates(device), "tv", CancellationToken.None);

        var error = await Assert.ThrowsAsync<TunerException>(() => pool.OpenAsync(Other(), Candidates(device, SampleMux.OtherTuning), "radio", CancellationToken.None));

        Assert.Equal("tuner.no_free_tuner", error.Code);
        Assert.False(running.Feed.IsFaulted);
    }

    [Fact]
    public async Task A_client_with_two_streams_keeps_them()
    {
        await using var pool = Pool(Transports());
        var device = SampleMux.Device(tuners: 1);
        var channel = SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid);

        await using var first = await pool.OpenAsync(channel, Candidates(device), "server", CancellationToken.None);
        await using var second = await pool.OpenAsync(channel, Candidates(device), "server", CancellationToken.None);

        var error = await Assert.ThrowsAsync<TunerException>(() => pool.OpenAsync(Other(), Candidates(device, SampleMux.OtherTuning), "server", CancellationToken.None));

        Assert.Equal("tuner.no_free_tuner", error.Code);
        Assert.False(first.Feed.IsFaulted);
    }

    [Fact]
    public async Task A_tuner_shared_with_somebody_else_is_not_taken()
    {
        await using var pool = Pool(Transports());
        var device = SampleMux.Device(tuners: 1);
        var channel = SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid);

        await using var mine = await pool.OpenAsync(channel, Candidates(device), "tv", CancellationToken.None);
        await using var theirs = await pool.OpenAsync(channel, Candidates(device), "radio", CancellationToken.None);

        var error = await Assert.ThrowsAsync<TunerException>(() => pool.OpenAsync(Other(), Candidates(device, SampleMux.OtherTuning), "tv", CancellationToken.None));

        Assert.Equal("tuner.no_free_tuner", error.Code);
    }

    [Fact]
    public async Task A_stream_without_a_client_replaces_nothing()
    {
        await using var pool = Pool(Transports());
        var device = SampleMux.Device(tuners: 1);

        await using var running = await pool.OpenAsync(SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid), Candidates(device), CancellationToken.None);

        var error = await Assert.ThrowsAsync<TunerException>(() => pool.OpenAsync(Other(), Candidates(device, SampleMux.OtherTuning), CancellationToken.None));

        Assert.Equal("tuner.no_free_tuner", error.Code);
    }

    private static StoredChannel Other() => SampleMux.Channel(SampleMux.OtherService, SampleMux.OtherPmtPid);

    private static FakeTransportFactory Transports() =>
        new FakeTransportFactory()
            .Add(SampleMux.Tuning, SampleMux.Packets())
            .Add(SampleMux.OtherTuning, SampleMux.Packets(1089));

    private static TunerPool Pool(ITsTransportFactory transports, FakeDescramblerFactory? descramblers = null) =>
        new(transports, descramblers ?? new FakeDescramblerFactory(), new DescramblerSlots(), Timings, NullLoggerFactory.Instance);

    private static IReadOnlyList<TuneCandidate> Candidates(TunerDevice device, Nightwave.Tuners.Dvb.Tuning.TuningParameters? tuning = null) =>
        [new TuneCandidate(device, new TuneRequest(tuning ?? SampleMux.Tuning, 1))];
}
