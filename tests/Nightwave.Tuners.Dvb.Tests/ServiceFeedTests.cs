using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb.Descrambling;
using Nightwave.Tuners.Dvb.Mpeg;
using Nightwave.Tuners.Dvb.Mpeg.Psi;
using Nightwave.Tuners.Dvb.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>
/// One service cut out of a transponder: its own PAT, its program map, its streams, and not a
/// packet more - and a scrambled one through the descrambler first.
/// </summary>
public sealed class ServiceFeedTests
{
    private static readonly FeedTimings Timings = new() { ProgramMap = TimeSpan.FromSeconds(3), Keys = TimeSpan.FromSeconds(3) };

    [Fact]
    public async Task Only_the_service_its_program_map_and_a_pat_of_its_own_come_out()
    {
        await using var feed = Feed(SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid), new FakeDescramblerFactory());
        using var reader = feed.Subscribe();

        feed.Process(TsBuilder.Join(SampleMux.Packets()));
        await feed.WaitReadyAsync(CancellationToken.None);

        var output = TsBuilder.Split(await ReadAvailableAsync(reader));
        var pids = output.Select(packet => TransportPacket.Pid(packet)).ToHashSet();

        Assert.Equal(
            new HashSet<int> { TransportPacket.PatPid, SampleMux.ClearPmtPid, SampleMux.ClearVideoPid, SampleMux.ClearAudioPid },
            pids);

        // Every PAT is this service's own: one program, the CRC right.
        var pats = Sections(output, TransportPacket.PatPid).Select(section => ProgramAssociationTable.Parse(section)).ToList();
        Assert.NotEmpty(pats);
        Assert.All(pats, pat =>
        {
            Assert.NotNull(pat);
            Assert.Equal(SampleMux.TransportStreamId, pat.TransportStreamId);
            Assert.Equal(new Dictionary<int, int> { [SampleMux.ClearService] = SampleMux.ClearPmtPid }, pat.Programs);
            Assert.Null(pat.NetworkPid);
        });

        // And its continuity counter runs without a gap, so a player never sees a lost packet.
        var counters = output.Where(packet => TransportPacket.Pid(packet) == 0).Select(packet => TransportPacket.ContinuityCounter(packet)).ToList();
        Assert.All(counters.Zip(counters.Skip(1)), pair => Assert.Equal((pair.First + 1) & 0x0F, pair.Second));
    }

    [Fact]
    public async Task Sections_reach_the_descrambler_while_packets_wait_for_a_key()
    {
        var descramblers = new FakeDescramblerFactory();

        await using var feed = Feed(SampleMux.Channel(SampleMux.ScrambledService, SampleMux.ScrambledPmtPid), descramblers);
        using var reader = feed.Subscribe();

        feed.Process(TsBuilder.Join(TsBuilder.Packets(SampleMux.ScrambledPmt(), SampleMux.ScrambledPmtPid)));
        await feed.WaitReadyAsync(CancellationToken.None);

        var descrambler = Assert.Single(descramblers.Opened);

        descrambler.ProbeResult = KeyProbe.Wrong;

        var packets = TsBuilder.Join([TsBuilder.EsPacket(SampleMux.ScrambledVideoPid, 0, 0xC1, scrambling: 3, start: true)]);
        var before = descrambler.SectionBytes;

        feed.Process(packets);

        Assert.Equal(before + packets.Length, descrambler.SectionBytes);
        Assert.True(descrambler.Probes > 0);
        Assert.DoesNotContain(descrambler.SeenPids, pid => pid == SampleMux.ScrambledVideoPid);
    }

    [Fact]
    public void A_single_program_pat_is_what_the_standard_says_it_is()
    {
        var section = SectionPacketizer.ProgramAssociation(0x0401, 5, 28006, 0x0064);

        Assert.True(MpegCrc32.IsValid(section));
        Assert.Equal(16, section.Length);

        var pat = ProgramAssociationTable.Parse(section)!;
        Assert.Equal(0x0401, pat.TransportStreamId);
        Assert.Equal(5, pat.Version);
        Assert.Equal(0x0064, pat.Programs[28006]);
    }

    [Fact]
    public async Task A_new_reader_starts_with_a_pat_and_the_program_map()
    {
        await using var feed = Feed(SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid), new FakeDescramblerFactory());

        feed.Process(TsBuilder.Join(SampleMux.Packets().Take(40)));
        await feed.WaitReadyAsync(CancellationToken.None);

        using var reader = feed.Subscribe();
        var start = TsBuilder.Split(await ReadAvailableAsync(reader));

        Assert.Equal(TransportPacket.PatPid, TransportPacket.Pid(start[0]));
        Assert.Equal(SampleMux.ClearPmtPid, TransportPacket.Pid(start[1]));
        Assert.Equal(SampleMux.ClearPmt(), Assert.Single(Sections(start, SampleMux.ClearPmtPid)));
    }

    [Fact]
    public async Task A_scrambled_service_goes_through_the_descrambler_and_its_ecms_stay_behind()
    {
        var descramblers = new FakeDescramblerFactory();
        var pidsChanged = 0;

        await using var feed = Feed(
            SampleMux.Channel(SampleMux.ScrambledService, SampleMux.ScrambledPmtPid, name: "Sky Cinema"),
            descramblers,
            () => Interlocked.Increment(ref pidsChanged));

        var packets = TsBuilder.Join(SampleMux.Packets());
        feed.Process(packets);
        await feed.WaitReadyAsync(CancellationToken.None);

        // Read from once the card has answered, as a viewer would be.
        using var reader = feed.Subscribe();
        feed.Process(packets);

        var descrambler = Assert.Single(descramblers.Opened);
        Assert.Equal(SampleMux.ScrambledService, descrambler.Request.ServiceId);
        Assert.Equal(SampleMux.ScrambledPmtPid, descrambler.Request.ProgramMapPid);
        Assert.Equal(SampleMux.ScrambledPmt(), descrambler.Request.ProgramMapSection.ToArray());
        Assert.Equal(SampleMux.TransportStreamId, descrambler.Request.TransportStreamId);
        Assert.Equal(SampleMux.NetworkId, descrambler.Request.OriginalNetworkId);
        Assert.Equal("Sky Cinema", descrambler.Request.ChannelName);
        Assert.Equal(0, descrambler.Request.Slot);

        // It saw the ECMs, and the tuner is asked for them - and for nothing it did not ask for.
        Assert.Contains(SampleMux.EcmPid, descrambler.SeenPids);
        Assert.Contains(SampleMux.EcmPid, feed.WantedPids);
        Assert.DoesNotContain(TransportPacket.CatPid, feed.WantedPids);
        Assert.True(pidsChanged > 0);

        var output = TsBuilder.Split(await ReadAvailableAsync(reader));
        var pids = output.Select(packet => TransportPacket.Pid(packet)).ToHashSet();

        Assert.DoesNotContain(SampleMux.EcmPid, pids);
        Assert.DoesNotContain(SampleMux.EmmPid, pids);
        Assert.DoesNotContain(TransportPacket.CatPid, pids);
        Assert.Contains(SampleMux.ScrambledVideoPid, pids);

        // What the descrambler cleared is what comes out.
        Assert.All(
            output.Where(packet => TransportPacket.Pid(packet) == SampleMux.ScrambledVideoPid),
            packet => Assert.Equal(0, TransportPacket.ScramblingControl(packet)));
    }

    /// <summary>
    /// Packets still scrambled after the descrambler - no key yet for their parity - are left
    /// out rather than passed on as noise a browser's decoder gives up on.
    /// </summary>
    [Fact]
    public async Task Packets_the_descrambler_could_not_clear_are_left_out()
    {
        var descramblers = new FakeDescramblerFactory();
        await using var feed = Feed(SampleMux.Channel(SampleMux.ScrambledService, SampleMux.ScrambledPmtPid), descramblers);

        var packets = TsBuilder.Join(SampleMux.Packets());
        feed.Process(packets);
        await feed.WaitReadyAsync(CancellationToken.None);
        Assert.Single(descramblers.Opened).KeylessPid = SampleMux.ScrambledVideoPid;

        using var reader = feed.Subscribe();
        feed.Process(packets);

        var output = TsBuilder.Split(await ReadAvailableAsync(reader));

        Assert.DoesNotContain(output, packet => TransportPacket.Pid(packet) == SampleMux.ScrambledVideoPid);
        Assert.All(output, packet => Assert.Equal(0, TransportPacket.ScramblingControl(packet)));
    }

    /// <summary>
    /// A channel is joined in the middle of a picture. What a reader is given starts at the next
    /// picture that stands on its own - the tables still arrive, so its PAT and program map are
    /// there - because the tail of a picture whose beginning nobody saw is what a decoder cannot
    /// use and complains about until the next whole one.
    /// </summary>
    [Fact]
    public async Task A_reader_is_handed_the_stream_from_a_picture_it_can_start_on()
    {
        await using var feed = Feed(SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid), new FakeDescramblerFactory());

        // The program map first, so the feed knows which pid carries the pictures.
        feed.Process(TsBuilder.Join(TsBuilder.Packets(SampleMux.ClearPmt(), SampleMux.ClearPmtPid)));
        await feed.WaitReadyAsync(CancellationToken.None);

        using var reader = feed.Subscribe();

        // Half a picture, then one that stands on its own.
        feed.Process(TsBuilder.Join([
            TsBuilder.EsPacket(SampleMux.ClearVideoPid, 1, 0xA1),
            TsBuilder.EsPacket(SampleMux.ClearAudioPid, 1, 0xA2),
            TsBuilder.RandomAccessPacket(SampleMux.ClearVideoPid, 2, 0xA3),
            TsBuilder.EsPacket(SampleMux.ClearAudioPid, 2, 0xA4),
        ]));

        var output = TsBuilder.Split(await ReadAvailableAsync(reader));

        // The start a new reader always gets: a PAT and the program map.
        Assert.Equal(TransportPacket.PatPid, TransportPacket.Pid(output[0]));
        Assert.Equal(SampleMux.ClearPmtPid, TransportPacket.Pid(output[1]));

        // And then the stream, from the whole picture on: nothing from before it.
        var stream = output.Skip(2).ToList();
        Assert.Equal(SampleMux.ClearVideoPid, TransportPacket.Pid(stream[0]));
        Assert.True(TransportPacket.IsRandomAccessPoint(stream[0]));
        Assert.Equal(0xA3, stream[0][6]);
        Assert.Equal([0xA4], stream.Skip(1).Select(packet => (int)packet[4]).ToArray());
    }

    /// <summary>
    /// A reader still waiting for its first picture is given the tables all the same: a PAT or a
    /// program map it never saw would be a gap in the counters of those pids, which is a demuxer
    /// reporting packets lost that were never lost.
    /// </summary>
    [Fact]
    public async Task A_reader_waiting_for_a_picture_still_gets_every_table()
    {
        await using var feed = Feed(SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid), new FakeDescramblerFactory());

        feed.Process(TsBuilder.Join(TsBuilder.Packets(SampleMux.ClearPmt(), SampleMux.ClearPmtPid)));
        await feed.WaitReadyAsync(CancellationToken.None);

        using var reader = feed.Subscribe();

        // Two rounds of tables with nothing but the middle of a picture between them.
        for (var round = 0; round < 2; round++)
        {
            feed.Process(TsBuilder.Join([
                .. TsBuilder.Packets(TsBuilder.Pat(SampleMux.TransportStreamId, 0, (SampleMux.ClearService, SampleMux.ClearPmtPid)), TransportPacket.PatPid, round),
                TsBuilder.EsPacket(SampleMux.ClearVideoPid, round, 0xA1),
                .. TsBuilder.Packets(SampleMux.ClearPmt(), SampleMux.ClearPmtPid, round + 1),
            ]));
        }

        var output = TsBuilder.Split(await ReadAvailableAsync(reader));

        // No picture yet, and the tables all there, each pid counting on without a gap.
        Assert.DoesNotContain(output, packet => TransportPacket.Pid(packet) == SampleMux.ClearVideoPid);
        Assert.Equal(3, output.Count(packet => TransportPacket.Pid(packet) == TransportPacket.PatPid));
        Assert.Equal(3, output.Count(packet => TransportPacket.Pid(packet) == SampleMux.ClearPmtPid));

        foreach (var pid in new[] { TransportPacket.PatPid, SampleMux.ClearPmtPid })
        {
            var counters = output.Where(packet => TransportPacket.Pid(packet) == pid).Select(packet => TransportPacket.ContinuityCounter(packet)).ToList();
            Assert.All(counters.Zip(counters.Skip(1)), pair => Assert.Equal((pair.First + 1) & 0x0F, pair.Second));
        }
    }

    /// <summary>
    /// A broadcaster that never says a picture stands on its own may not keep a viewer looking at
    /// nothing: after the wait, the stream is handed over as it is.
    /// </summary>
    [Fact]
    public async Task A_stream_that_names_no_picture_to_start_on_is_handed_over_anyway()
    {
        await using var feed = Feed(
            SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid),
            new FakeDescramblerFactory(),
            timings: new FeedTimings { ProgramMap = TimeSpan.FromSeconds(3), Keys = TimeSpan.FromSeconds(3), CleanStart = TimeSpan.FromMilliseconds(150) });

        feed.Process(TsBuilder.Join(TsBuilder.Packets(SampleMux.ClearPmt(), SampleMux.ClearPmtPid)));
        await feed.WaitReadyAsync(CancellationToken.None);

        using var reader = feed.Subscribe();

        var middle = TsBuilder.Join([TsBuilder.EsPacket(SampleMux.ClearVideoPid, 1, 0xA1)]);
        feed.Process(middle);

        await Task.Delay(200);
        feed.Process(middle);

        var output = TsBuilder.Split(await ReadAvailableAsync(reader));
        var video = output.Where(packet => TransportPacket.Pid(packet) == SampleMux.ClearVideoPid).ToList();

        // The first one was held back, the one after the wait was not.
        Assert.Single(video);
    }

    /// <summary>
    /// A radio service has no pictures to wait for, and is handed over as it arrives.
    /// </summary>
    [Fact]
    public async Task A_service_without_pictures_is_not_kept_waiting_for_one()
    {
        var pmt = TsBuilder.Pmt(SampleMux.ClearService, 0, SampleMux.ClearAudioPid, [], (0x04, SampleMux.ClearAudioPid, []));

        await using var feed = Feed(SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid), new FakeDescramblerFactory());

        feed.Process(TsBuilder.Join(TsBuilder.Packets(pmt, SampleMux.ClearPmtPid)));
        await feed.WaitReadyAsync(CancellationToken.None);

        using var reader = feed.Subscribe();
        feed.Process(TsBuilder.Join([TsBuilder.EsPacket(SampleMux.ClearAudioPid, 1, 0xA2)]));

        var output = TsBuilder.Split(await ReadAvailableAsync(reader));

        Assert.Contains(output, packet => TransportPacket.Pid(packet) == SampleMux.ClearAudioPid);
    }

    /// <summary>
    /// The packets before the card's first key are the service being joined, not a fault: they
    /// are left out like any other, and not counted against the descrambler afterwards.
    /// </summary>
    [Fact]
    public async Task Packets_from_before_the_first_key_are_not_held_against_the_descrambler()
    {
        var descramblers = new FakeDescramblerFactory();
        await using var feed = Feed(SampleMux.Channel(SampleMux.ScrambledService, SampleMux.ScrambledPmtPid), descramblers);

        // The program map opens the descrambler; until it answers, everything scrambled is dropped.
        descramblers.HoldKeys = true;
        feed.Process(TsBuilder.Join(TsBuilder.Packets(SampleMux.ScrambledPmt(), SampleMux.ScrambledPmtPid)));
        await WaitUntilAsync(() => descramblers.Opened.Count > 0);

        descramblers.Opened[0].KeylessPid = SampleMux.ScrambledVideoPid;
        feed.Process(TsBuilder.Join([TsBuilder.EsPacket(SampleMux.ScrambledVideoPid, 1, 0xC1, scrambling: 2)]));

        Assert.Equal(0, feed.StillScrambledPackets);

        // Once the card has answered, what it still cannot clear is counted.
        descramblers.Opened[0].Keys = true;
        await feed.WaitReadyAsync(CancellationToken.None);
        feed.Process(TsBuilder.Join([TsBuilder.EsPacket(SampleMux.ScrambledVideoPid, 2, 0xC1, scrambling: 2, start: true)]));

        Assert.Equal(1, feed.StillScrambledPackets);
    }

    /// <summary>
    /// The ECM pids are asked for from the start, so the server's first filter finds an ECM
    /// waiting. Once the card has answered, one the server never filtered for is not needed.
    /// </summary>
    [Fact]
    public async Task Ecm_pids_nobody_filters_for_are_let_go_after_the_first_key()
    {
        var descramblers = new FakeDescramblerFactory { HoldKeys = true };
        await using var feed = Feed(SampleMux.Channel(SampleMux.ScrambledService, SampleMux.ScrambledPmtPid), descramblers);

        feed.Process(TsBuilder.Join(TsBuilder.Packets(SampleMux.ScrambledPmt(), SampleMux.ScrambledPmtPid)));
        await WaitUntilAsync(() => descramblers.Opened.Count > 0);
        descramblers.Opened[0].Require();

        Assert.Contains(SampleMux.EcmPid, feed.WantedPids);
        Assert.NotNull(descramblers.Opened[0].Request.RecentEcms);

        descramblers.Opened[0].Keys = true;
        await feed.WaitReadyAsync(CancellationToken.None);

        Assert.DoesNotContain(SampleMux.EcmPid, feed.WantedPids);
    }

    [Fact]
    public async Task Pids_the_descrambler_asks_for_later_are_passed_to_the_tuner()
    {
        var descramblers = new FakeDescramblerFactory();

        await using var feed = Feed(SampleMux.Channel(SampleMux.ScrambledService, SampleMux.ScrambledPmtPid), descramblers);
        feed.Process(TsBuilder.Join(SampleMux.Packets()));
        await feed.WaitReadyAsync(CancellationToken.None);

        descramblers.Opened[0].Require(SampleMux.EcmPid, TransportPacket.CatPid, SampleMux.EmmPid);

        Assert.Contains(SampleMux.EmmPid, feed.WantedPids);
        Assert.Contains(TransportPacket.CatPid, feed.WantedPids);

        descramblers.Opened[0].Require(SampleMux.EcmPid);

        Assert.DoesNotContain(SampleMux.EmmPid, feed.WantedPids);
    }

    [Fact]
    public async Task What_the_descrambler_reports_as_wrong_is_kept_and_cleared_again()
    {
        var descramblers = new FakeDescramblerFactory();

        await using var feed = Feed(SampleMux.Channel(SampleMux.ScrambledService, SampleMux.ScrambledPmtPid, name: "Sky Cinema"), descramblers);
        feed.Process(TsBuilder.Join(SampleMux.Packets()));
        await feed.WaitReadyAsync(CancellationToken.None);

        var problem = new TunerException("satip", "descrambler.connection_lost", "The connection to the descrambling server at 192.0.2.30:9000 was lost.", "Check the server.");
        descramblers.Opened[0].Report(problem);

        Assert.Same(problem, feed.DescramblingProblem);
        Assert.False(feed.IsFaulted);

        descramblers.Opened[0].Report(null);

        Assert.Null(feed.DescramblingProblem);
    }

    [Fact]
    public async Task A_new_program_map_is_passed_to_the_descrambler()
    {
        var descramblers = new FakeDescramblerFactory();

        await using var feed = Feed(SampleMux.Channel(SampleMux.ScrambledService, SampleMux.ScrambledPmtPid), descramblers);
        feed.Process(TsBuilder.Join(TsBuilder.Packets(SampleMux.ScrambledPmt(0), SampleMux.ScrambledPmtPid)));
        await feed.WaitReadyAsync(CancellationToken.None);

        feed.Process(TsBuilder.Join(TsBuilder.Packets(SampleMux.ScrambledPmt(1), SampleMux.ScrambledPmtPid, continuity: 1)));

        var descrambler = descramblers.Opened[0];
        await WaitUntilAsync(() => descrambler.ProgramMapUpdates.Count > 0);

        Assert.Equal(SampleMux.ScrambledPmt(1), descrambler.ProgramMapUpdates[^1]);
    }

    [Fact]
    public async Task Services_descrambled_at_the_same_time_get_different_slots_and_give_them_back()
    {
        var descramblers = new FakeDescramblerFactory();
        var slots = new DescramblerSlots();

        var first = Feed(SampleMux.Channel(SampleMux.ScrambledService, SampleMux.ScrambledPmtPid), descramblers, slots: slots);
        var second = Feed(SampleMux.Channel(SampleMux.ScrambledService, SampleMux.ScrambledPmtPid), descramblers, slots: slots);

        var packets = TsBuilder.Join(SampleMux.Packets().Take(60));
        first.Process(packets);
        second.Process(packets);
        await first.WaitReadyAsync(CancellationToken.None);
        await second.WaitReadyAsync(CancellationToken.None);

        Assert.Equal(new[] { 0, 1 }, descramblers.Opened.Select(opened => opened.Request.Slot).Order().ToArray());

        await first.DisposeAsync();

        Assert.Equal(1, slots.InUse);
        Assert.True(descramblers.Opened.Single(opened => opened.Request.Slot == 0).Disposed);

        await second.DisposeAsync();

        Assert.Equal(0, slots.InUse);
    }

    [Fact]
    public async Task A_scrambled_service_without_descrambling_fails_and_says_so()
    {
        await using var feed = Feed(
            SampleMux.Channel(SampleMux.ScrambledService, SampleMux.ScrambledPmtPid, name: "Sky Cinema"),
            new FakeDescramblerFactory { IsConfigured = false });
        using var reader = feed.Subscribe();

        feed.Process(TsBuilder.Join(SampleMux.Packets().Take(60)));

        var error = await Assert.ThrowsAsync<TunerException>(() => feed.WaitReadyAsync(CancellationToken.None));
        Assert.Equal("tuner.descrambling_not_configured", error.Code);
        Assert.Equal("Sky Cinema", error.Values["channel"]);
        Assert.Contains("Sky Cinema", error.Message, StringComparison.Ordinal);

        // A reader that was already there hears the same.
        await Assert.ThrowsAsync<TunerException>(async () =>
        {
            var buffer = new byte[65536];
            while (await reader.ReadAsync(buffer) > 0)
            {
            }
        });
    }

    [Fact]
    public async Task Scrambling_bits_without_ca_descriptors_still_count_as_scrambled()
    {
        var descramblers = new FakeDescramblerFactory { IsConfigured = false };
        var pmt = TsBuilder.Pmt(SampleMux.ScrambledService, 0, SampleMux.ScrambledVideoPid, [], (0x1B, SampleMux.ScrambledVideoPid, []));

        await using var feed = Feed(SampleMux.Channel(SampleMux.ScrambledService, SampleMux.ScrambledPmtPid), descramblers);

        feed.Process(TsBuilder.Join(TsBuilder.Packets(pmt, SampleMux.ScrambledPmtPid)));
        await feed.WaitReadyAsync(CancellationToken.None);
        feed.Process(TsBuilder.EsPacket(SampleMux.ScrambledVideoPid, 0, 0xC1, scrambling: 3));

        Assert.True(feed.IsFaulted);
        Assert.Equal("tuner.descrambling_not_configured", Assert.IsType<TunerException>(feed.Fault).Code);
    }

    [Fact]
    public async Task A_service_gone_from_the_pat_fails_with_the_transponder_named()
    {
        await using var feed = Feed(SampleMux.Channel(999, 0x999), new FakeDescramblerFactory());

        feed.Process(TsBuilder.Join(SampleMux.Packets().Take(20)));

        var error = await Assert.ThrowsAsync<TunerException>(() => feed.WaitReadyAsync(CancellationToken.None));
        Assert.Equal("tuner.service_missing", error.Code);
        Assert.Equal("11836 MHz H 27500 DVB-S", error.Values["transponder"]);
    }

    /// <summary>
    /// The previous transponder's PAT, which a Kathrein EXIP-418 still sends after a switch, does
    /// not count as this service gone: this transponder's own PAT follows.
    /// </summary>
    [Fact]
    public async Task The_previous_transponders_pat_is_not_taken_for_the_service_gone()
    {
        await using var feed = Feed(SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid), new FakeDescramblerFactory());

        feed.Process(TsBuilder.Join(TsBuilder.Packets(TsBuilder.Pat(4711, 0, (28, 0x64)), 0)));
        Assert.False(feed.IsFaulted);

        feed.Process(TsBuilder.Join(SampleMux.Packets()));
        await feed.WaitReadyAsync(CancellationToken.None);
        Assert.False(feed.IsFaulted);
    }

    [Fact]
    public async Task No_program_map_in_time_is_no_signal()
    {
        await using var feed = Feed(
            SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid),
            new FakeDescramblerFactory(),
            timings: new FeedTimings { ProgramMap = TimeSpan.FromMilliseconds(100) });

        var error = await Assert.ThrowsAsync<TunerException>(() => feed.WaitReadyAsync(CancellationToken.None));
        Assert.Equal("tuner.no_signal", error.Code);
    }

    [Fact]
    public void A_slow_reader_loses_data_instead_of_holding_the_tuner_up()
    {
        using var reader = new ServiceReader(_ => { }, capacity: 4 * TransportPacket.Size);
        var chunk = new byte[2 * TransportPacket.Size];

        Assert.True(reader.Offer(chunk));
        Assert.True(reader.Offer(chunk));
        Assert.False(reader.Offer(chunk));
        Assert.Equal(chunk.Length, reader.TakeDropped());
    }

    [Fact]
    public async Task A_reader_that_caught_up_is_offered_data_again()
    {
        using var reader = new ServiceReader(_ => { }, capacity: 2 * TransportPacket.Size);
        var chunk = new byte[2 * TransportPacket.Size];

        Assert.True(reader.Offer(chunk));
        Assert.False(reader.Offer(chunk));

        var buffer = new byte[chunk.Length];
        Assert.Equal(chunk.Length, await reader.ReadAsync(buffer));

        Assert.True(reader.Offer(chunk));
    }

    private static ServiceFeed Feed(
        Nightwave.Tuners.Dvb.Channels.StoredChannel channel,
        FakeDescramblerFactory descramblers,
        Action? pidsChanged = null,
        DescramblerSlots? slots = null,
        FeedTimings? timings = null) =>
        new(
            channel,
            SampleMux.Device(),
            channel.Tuning!.Describe(),
            descramblers,
            slots ?? new DescramblerSlots(),
            pidsChanged ?? (() => { }),
            timings ?? Timings,
            NullLogger.Instance);

    /// <summary>Everything queued for a reader right now, or a second of it from a tuner that keeps sending.</summary>
    internal static async Task<byte[]> ReadAvailableAsync(Stream reader)
    {
        var output = new List<byte>();
        var buffer = new byte[64 * 1024];
        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (clock.Elapsed < TimeSpan.FromSeconds(1))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            try
            {
                var read = await reader.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                {
                    break;
                }

                output.AddRange(buffer.Take(read));
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return [.. output];
    }

    internal static List<byte[]> Sections(IEnumerable<byte[]> packets, int pid)
    {
        var assembler = new SectionAssembler();
        var sections = new List<byte[]>();

        foreach (var packet in packets.Where(packet => TransportPacket.Pid(packet) == pid))
        {
            assembler.Push(packet, section => sections.Add(section.ToArray()));
        }

        return sections;
    }

    internal static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The condition did not become true within five seconds.");
            }

            await Task.Delay(10);
        }
    }
}
