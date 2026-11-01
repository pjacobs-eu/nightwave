using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb.Channels;
using Nightwave.Tuners.Dvb.Mpeg.Psi;
using Nightwave.Tuners.Dvb.Mpeg.Si;
using Nightwave.Tuners.Dvb.Scanning;
using Nightwave.Tuners.Dvb.Transport;
using Nightwave.Tuners.Dvb.Tuning;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>
/// What a scan does with a service that is not transmitting at the moment it runs.
/// </summary>
/// <remarks>
/// A channel that broadcasts in the evenings, a regional window, a season that has not started:
/// at the wrong hour they carry nothing, and there is no sound and no picture to work out what
/// they are from. They are still channels, and they must not vanish from somebody's list
/// because of when the scan happened to run.
/// </remarks>
public sealed class OffAirChannelTests
{
    [Fact]
    public void A_service_that_is_transmitting_nothing_is_still_a_channel()
    {
        // No description in the SDT, and no program map: what off the air looks like.
        var channel = Channel(new ScannedService(ServiceId: 42, PmtPid: 260, ProgramMap: null, Description: null));

        Assert.NotNull(channel);
        Assert.Equal(ChannelKind.Television, channel.Kind);
        Assert.Equal(42, channel.ServiceId);

        // The pid the program map will be read from when somebody tunes it, which is what makes
        // the empty pid list below cost nothing.
        Assert.Equal(260, channel.PmtPid);
        Assert.Empty(channel.Pids);
    }

    /// <summary>A program map that is being sent and lists nothing is the same situation.</summary>
    [Fact]
    public void A_service_whose_program_map_is_empty_is_still_a_channel()
    {
        var channel = Channel(new ScannedService(42, 260, Map([]), Description: null));

        Assert.NotNull(channel);
        Assert.Equal(ChannelKind.Television, channel.Kind);
    }

    /// <summary>
    /// The description says what it is, so nothing has to be guessed - and it stays what the
    /// description said even with nothing on the air.
    /// </summary>
    [Theory]
    [InlineData(0x01, ChannelKind.Television)]
    [InlineData(0x02, ChannelKind.Radio)]
    public void A_described_service_off_the_air_keeps_the_kind_it_was_described_as(int serviceType, ChannelKind expected)
    {
        var channel = Channel(new ScannedService(42, 260, ProgramMap: null, Description: Description(serviceType)));

        Assert.NotNull(channel);
        Assert.Equal(expected, channel.Kind);
    }

    /// <summary>
    /// The one thing that is actually known about a service and says it is not a channel. A
    /// data carousel stays out however little it is transmitting.
    /// </summary>
    [Fact]
    public void A_service_the_transponder_called_something_else_is_still_left_out()
    {
        Assert.Null(Channel(new ScannedService(42, 260, ProgramMap: null, Description: Description(0x0C))));
    }

    /// <summary>
    /// Also known: a program map that is being sent, lists streams, and not one of them is
    /// sound or picture.
    /// </summary>
    [Fact]
    public void A_service_that_carries_only_data_is_left_out()
    {
        var data = new ElementaryStream(0x0B, 300, StreamKind.Data, null, null, []);

        Assert.Null(Channel(new ScannedService(42, 260, Map([data]), Description: null)));
    }

    [Fact]
    public void A_service_that_is_on_the_air_is_read_from_what_it_carries()
    {
        var video = new ElementaryStream(0x02, 300, StreamKind.Video, "mpeg2video", null, []);
        var audio = new ElementaryStream(0x03, 301, StreamKind.Audio, "mp2", "deu", []);

        var television = Channel(new ScannedService(42, 260, Map([video, audio]), Description: null));
        var radio = Channel(new ScannedService(43, 261, Map([audio]), Description: null));

        Assert.Equal(ChannelKind.Television, television!.Kind);
        Assert.Equal(ChannelKind.Radio, radio!.Kind);
        Assert.Equal([300, 301], television.Pids.Order());
    }

    /// <summary>
    /// Sky takes a channel on a break out of the PAT and leaves it in the SDT: on 12304 H, Sky
    /// Sport Bundesliga 3 between matches. The SDT is all there is to find it by, and it is
    /// found by it - with its pay TV flag, and without a program map. Software update services
    /// left out of the PAT the same way stay out.
    /// </summary>
    [Fact]
    public async Task A_service_only_the_sdt_describes_is_still_a_channel()
    {
        var pat = TsBuilder.Pat(1101, 0, (100, 0x100));
        var pmt = TsBuilder.Pmt(100, 0, 0x101, [], (0x1B, 0x101, []));
        var sdt = TsBuilder.Sdt(
            1101,
            133,
            0,
            (100, true, TsBuilder.ServiceDescriptor(0x19, "Sky", "Sky Sport Bundesliga")),
            (287, true, TsBuilder.ServiceDescriptor(0x19, "Sky", "Sky Sport Bundesliga 3")),
            (4028, false, TsBuilder.ServiceDescriptor(0x0C, "Sky", "Pace TDS 865 NSD")));

        var packets = new List<byte[]>();

        for (var round = 0; round < 4; round++)
        {
            packets.AddRange(TsBuilder.Packets(pat, 0, round));
            packets.AddRange(TsBuilder.Packets(pmt, 0x100, round));
            packets.AddRange(TsBuilder.Packets(sdt, 0x11, round));
        }

        var transport = new FakeTransport(new TuneRequest(SampleMux.Tuning, 1), packets);
        var result = await MuxScanner.ScanAsync(transport, Timings, readNetwork: false, CancellationToken.None);

        Assert.Equal([100, 287, 4028], result.Services.Select(service => service.ServiceId));

        var channels = result.Services.Select(service => Channel(service, result)).OfType<StoredChannel>().ToList();
        var paused = Assert.Single(channels, channel => channel.ServiceId == 287);

        Assert.Equal(2, channels.Count);
        Assert.Equal("Sky Sport Bundesliga 3", paused.Name);
        Assert.Equal(ChannelKind.Television, paused.Kind);
        Assert.True(paused.Scrambled);
        Assert.Equal(133, paused.OriginalNetworkId);
        Assert.Equal(0, paused.PmtPid);
        Assert.Empty(paused.Pids);
    }

    /// <summary>
    /// A Kathrein EXIP-418 tuned from 11836 H to 12304 H sent 11836 H's PAT first. With its SDT
    /// next to the PAT of the other transponder, every service of one turned into a channel of
    /// the other: Sky Sport Bundesliga twice, once under a transport stream it is not on.
    /// </summary>
    [Fact]
    public async Task Tables_left_over_from_the_previous_transponder_are_not_taken_for_this_one()
    {
        var stalePat = TsBuilder.Pat(1019, 0, (100, 0x100));
        var staleSdt = TsBuilder.Sdt(1019, 1, 0, (100, false, TsBuilder.ServiceDescriptor(0x19, "ARD", "Das Erste HD")));
        var pat = TsBuilder.Pat(12, 0, (105, 0x60));
        var pmt = TsBuilder.Pmt(105, 0, 0x101, [], (0x1B, 0x101, []));
        var sdt = TsBuilder.Sdt(
            12,
            133,
            0,
            (105, true, TsBuilder.ServiceDescriptor(0x19, "Sky", "Sky Sport Bundesliga")),
            (287, true, TsBuilder.ServiceDescriptor(0x19, "Sky", "Sky Sport Bundesliga 3")));

        var packets = new List<byte[]>();

        for (var round = 0; round < 4; round++)
        {
            packets.AddRange(TsBuilder.Packets(pat, 0, round + 1));
            packets.AddRange(TsBuilder.Packets(pmt, 0x60, round));
            packets.AddRange(TsBuilder.Packets(sdt, 0x11, round + 1));
        }

        // The leftovers come first: the previous transponder's PAT and SDT, then this one's SDT
        // before its own PAT.
        var transport = new FakeTransport(new TuneRequest(SampleMux.Tuning, 1), packets)
        {
            Leftovers = new([.. TsBuilder.Packets(stalePat, 0, 0), .. TsBuilder.Packets(staleSdt, 0x11, 0), .. TsBuilder.Packets(sdt, 0x11, 0)]),
        };
        var result = await MuxScanner.ScanAsync(transport, Timings, readNetwork: false, CancellationToken.None);

        Assert.Equal(12, result.TransportStreamId);
        Assert.Equal(133, result.OriginalNetworkId);
        Assert.Equal([105, 287], result.Services.Select(service => service.ServiceId));
        Assert.NotNull(result.Services[0].ProgramMap);
        Assert.Equal("Sky Sport Bundesliga", result.Services[0].Description?.Name);
    }

    /// <summary>
    /// What a whole channel scan lost RTL HD to: 10832 H gave the PAT of 10817 V, then silence. Its
    /// services then replaced the channels of 10817 V, and RTL HD never showed up at all.
    /// </summary>
    [Fact]
    public async Task A_leftover_table_and_then_silence_is_a_failure_and_no_transponder()
    {
        var stalePat = TsBuilder.Pat(1056, 0, (29951, 0x100), (29956, 0x101));
        var transport = new FakeTransport(new TuneRequest(SampleMux.Tuning, 1), TsBuilder.Packets(stalePat, 0, 0));

        await Assert.ThrowsAsync<TunerTransportException>(() => MuxScanner.ScanAsync(transport, Timings, readNetwork: false, CancellationToken.None));
    }

    [Fact]
    public async Task With_the_nit_to_go_by_a_table_of_another_transport_stream_is_not_looked_at()
    {
        var stalePat = TsBuilder.Pat(1056, 0, (29951, 0x100));
        var pat = TsBuilder.Pat(1057, 0, (61200, 0x60));
        var pmt = TsBuilder.Pmt(61200, 0, 0x101, [], (0x1B, 0x101, []));
        var sdt = TsBuilder.Sdt(1057, 1, 0, (61200, false, TsBuilder.ServiceDescriptor(0x19, "RTL", "RTL HD")));
        var packets = new List<byte[]>();

        for (var round = 0; round < 4; round++)
        {
            packets.AddRange(TsBuilder.Packets(stalePat, 0, round * 2));
            packets.AddRange(TsBuilder.Packets(pat, 0, (round * 2) + 1));
            packets.AddRange(TsBuilder.Packets(pmt, 0x60, round));
            packets.AddRange(TsBuilder.Packets(sdt, 0x11, round));
        }

        var transport = new FakeTransport(new TuneRequest(SampleMux.Tuning, 1), packets);

        var result = await MuxScanner.ScanAsync(transport, Timings, readNetwork: false, CancellationToken.None, expectedTransportStream: 1057);

        Assert.Equal(1057, result.TransportStreamId);
        Assert.Equal("RTL HD", Assert.Single(result.Services).Description?.Name);
    }

    [Fact]
    public async Task When_the_expected_transport_stream_never_comes_the_scan_says_so()
    {
        var transport = new FakeTransport(new TuneRequest(SampleMux.Tuning, 1), TsBuilder.Packets(TsBuilder.Pat(1056, 0, (29951, 0x100)), 0, 0));

        await Assert.ThrowsAsync<TunerTransportException>(() => MuxScanner.ScanAsync(transport, Timings, readNetwork: false, CancellationToken.None, expectedTransportStream: 1057));
    }

    private static readonly ScanTimings Timings = new()
    {
        Lock = TimeSpan.FromMilliseconds(200),
        Pat = TimeSpan.FromMilliseconds(500),
        ProgramMaps = TimeSpan.FromMilliseconds(500),
        Sdt = TimeSpan.FromMilliseconds(500),
        Transponder = TimeSpan.FromSeconds(3),
    };

    private static ServiceDescription Description(int serviceType) =>
        new(ServiceId: 42, RunningStatus: 1, IsScrambled: false, ServiceType: serviceType, ProviderName: "ARD", Name: "Das Erste");

    private static ProgramMapTable Map(IReadOnlyList<ElementaryStream> streams) => new()
    {
        ProgramNumber = 42,
        Version = 0,
        PcrPid = 300,
        Descriptors = [],
        Streams = streams,
        Section = [],
    };

    private static StoredChannel? Channel(ScannedService service) =>
        Channel(service, new MuxScanResult
        {
            TransportStreamId = 1101,
            OriginalNetworkId = 1,
            Services = [service],
            Network = [],
        });

    private static StoredChannel? Channel(ScannedService service, MuxScanResult transponder) =>
        ChannelFactory.FromService(
            new ScanInput
            {
                TargetId = "sat:192",
                Device = SampleMux.Device(),
                Delivery = DeliverySystem.Satellite,
                Position = SampleMux.Position,
            },
            SampleMux.Tuning,
            transponder,
            service,
            originalNetworkId: 1,
            logicalChannel: null);
}
