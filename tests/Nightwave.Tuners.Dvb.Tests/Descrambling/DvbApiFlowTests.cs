using System.Net;
using System.Net.Sockets;
using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb.Descrambling;
using Nightwave.Tuners.Dvb.Descrambling.Csa;
using Nightwave.Tuners.Dvb.Mpeg;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nightwave.Tuners.Dvb.Tests.Descrambling;

/// <summary>
/// The whole way, against a fake descrambling server on loopback: greeting, CA PMT, filters,
/// sections back, keys, clear packets, and what happens when the connection goes.
/// </summary>
public class DvbApiFlowTests
{
    private static readonly byte[] ControlWord = [0x12, 0x34, 0x56, 0x9C, 0x78, 0x9A, 0xBC, 0xCE];

    private static readonly DvbApiTimings Fast = new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(2),
        PlainGreetingTimeout = TimeSpan.FromMilliseconds(200),
        HandshakeTimeout = TimeSpan.FromMilliseconds(500),
        ControlWordTimeout = TimeSpan.FromMilliseconds(300),
        ReconnectInitialDelay = TimeSpan.FromMilliseconds(50),
        ReconnectMaxDelay = TimeSpan.FromMilliseconds(200),
    };

    private static DvbApiDescramblerFactory Factory(DvbApiOptions? options, DvbApiTimings? timings = null) =>
        new(new TestOptionsSource(options), NullLoggerFactory.Instance, timings ?? Fast, TimeProvider.System);

    private static ServiceDescramblingRequest Request(int slot = 2, string channel = "Sky Cinema") => new()
    {
        ServiceId = Ts.ServiceId,
        ProgramMapSection = Ts.ProgramMap(),
        ProgramMapPid = Ts.ProgramMapPid,
        Slot = slot,
        TransportStreamId = 0x0441,
        OriginalNetworkId = 0x0085,
        ChannelName = channel,
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the condition.");
            await Task.Delay(10);
        }
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task A_scrambled_service_comes_out_clear(int protocolVersion)
    {
        await using var server = new FakeDescramblingServer();
        await using var factory = Factory(server.Options, Fast with { ProtocolVersion = protocolVersion });

        await using var descrambler = await factory.OpenAsync(Request(slot: 2), CancellationToken.None);

        // The greeting, then the service as "add" with the slot's device number (23 - slot) as
        // adapter, demux and ca device, and marked as live television so a server with a stream
        // relay still sends it every key.
        Assert.Equal("Nightwave test", server.ClientName);
        Assert.Equal(protocolVersion, server.ClientProtocolVersion);
        var caPmt = await server.ReceiveAsync(frame => frame.Kind == FakeDescramblingServer.FrameKind.CaPmt);
        Assert.Equal(DvbApiProtocol.ListManagementAdd, caPmt.Body[0]);
        Assert.Equal(Ts.ServiceId, (caPmt.Body[1] << 8) | caPmt.Body[2]);
        Assert.Equal(Convert.FromHexString("83011584020100850400000001860115870115"), caPmt.Body[17..36]);
        if (protocolVersion >= 3)
        {
            Assert.NotEqual(0u, caPmt.MessageId);
        }

        // The server asks for the ECMs; the tuner is told, and the ECM goes back as filter data.
        var pidsChanged = new TaskCompletionSource();
        descrambler.RequiredPidsChanged += (_, _) => pidsChanged.TrySetResult();
        await server.SendAsync(server.SetFilter(adapter: 21, demux: 0, filter: 1, Ts.EcmPid, 0x80, 0xFE));
        await pidsChanged.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([Ts.EcmPid], descrambler.RequiredPids);

        var ecm = Ts.Ecm(0x80, 7);
        var continuity = 0;
        descrambler.Process(Ts.Packets(Ts.EcmPid, ref continuity, ecm));
        var filterData = await server.ReceiveAsync(frame => frame.Kind == FakeDescramblingServer.FrameKind.FilterData);
        Assert.Equal(0, filterData.Body[0]);
        Assert.Equal(1, filterData.Body[1]);
        Assert.Equal(ecm, filterData.Body[2..]);

        // The key, and the stream is clear.
        await server.SendAsync([
            .. server.SetPid(21, Ts.VideoPid, 0),
            .. server.SetDescrambler(21, 0, 0, ControlWord),
            .. server.EcmInfo(21, 0x0963, Ts.EcmPid, "sky-card"),
        ]);
        await WaitUntilAsync(() => descrambler.HasKeys && ((IServiceDescramblerDiagnostics)descrambler).Status.LastEcm is not null);

        var packet = Ts.ScrambledPacket(Ts.VideoPid, CsaKey.Create(ControlWord), odd: false, continuity: 3);
        var buffer = packet.Scrambled.ToArray();
        descrambler.Process(buffer);
        Assert.Equal(packet.Clear, buffer);

        var status = ((IServiceDescramblerDiagnostics)descrambler).Status;
        Assert.True(status.Connected);
        Assert.Equal("sky-card", status.LastEcm!.Reader);
        Assert.Equal(1, status.EcmSectionsForwarded);
        Assert.Null(status.Problem);
    }

    /// <summary>
    /// The ECM that went past while the CA PMT was on its way is handed to the server the moment
    /// it filters for it, instead of the server waiting for the broadcaster's next one.
    /// </summary>
    [Fact]
    public async Task An_ecm_from_before_the_filter_is_handed_over_when_the_filter_is_set()
    {
        await using var server = new FakeDescramblingServer();
        await using var factory = Factory(server.Options);

        var ecm = Ts.Ecm(0x81, 3);
        var continuity = 0;
        var recent = new RecentEcms(TimeProvider.System);
        var packets = Ts.Packets(Ts.EcmPid, ref continuity, ecm);
        for (var offset = 0; offset < packets.Length; offset += TransportPacket.Size)
        {
            recent.Keep(Ts.EcmPid, packets.AsSpan(offset, TransportPacket.Size));
        }

        await using var descrambler = await factory.OpenAsync(Request() with { RecentEcms = recent }, CancellationToken.None);
        await server.ReceiveAsync(frame => frame.Kind == FakeDescramblingServer.FrameKind.CaPmt);

        await server.SendAsync(server.SetFilter(adapter: 21, demux: 0, filter: 1, Ts.EcmPid, 0x80, 0xFE));

        var filterData = await server.ReceiveAsync(frame => frame.Kind == FakeDescramblingServer.FrameKind.FilterData);
        Assert.Equal(1, filterData.Body[1]);
        Assert.Equal(ecm, filterData.Body[2..]);
        Assert.Equal(1, ((IServiceDescramblerDiagnostics)descrambler).Status.EcmSectionsForwarded);
    }

    [Fact]
    public async Task Emms_that_match_the_servers_filters_are_forwarded()
    {
        await using var server = new FakeDescramblingServer();
        await using var factory = Factory(server.Options);
        await using var descrambler = await factory.OpenAsync(Request(), CancellationToken.None);
        await server.ReceiveAsync(frame => frame.Kind == FakeDescramblingServer.FrameKind.CaPmt);

        // The server first wants the CAT, then the EMMs of its card on the pid the CAT names.
        const int emmPid = 0x600;
        await server.SendAsync([
            .. server.SetFilter(21, 0, 2, 0x0001, 0x01, 0xFF),
            .. server.SetFilter(21, 0, 3, emmPid, 0x82, 0xFF),
        ]);
        await WaitUntilAsync(() => descrambler.RequiredPids.Count == 2);

        var cat = Ts.Section(0x01, [0xFF, 0xFF, 0xC1, 0x00, 0x00, 0x09, 0x04, 0x09, 0x63, 0xE6, 0x00]);
        var ownEmm = Ts.Ecm(0x82, 1);
        var otherEmm = Ts.Ecm(0x83, 2);
        var catContinuity = 0;
        var emmContinuity = 0;
        descrambler.Process([
            .. Ts.Packets(0x0001, ref catContinuity, cat),
            .. Ts.Packets(emmPid, ref emmContinuity, otherEmm, ownEmm),
        ]);

        var first = await server.ReceiveAsync(frame => frame.Kind == FakeDescramblingServer.FrameKind.FilterData);
        var second = await server.ReceiveAsync(frame => frame.Kind == FakeDescramblingServer.FrameKind.FilterData);
        Assert.Equal(cat, first.Body[2..]);
        Assert.Equal(3, second.Body[1]);
        Assert.Equal(ownEmm, second.Body[2..]);
        Assert.Equal(2, ((IServiceDescramblerDiagnostics)descrambler).Status.OtherSectionsForwarded);
    }

    [Fact]
    public async Task After_a_dropped_connection_every_service_is_handed_over_again()
    {
        await using var server = new FakeDescramblingServer();
        await using var factory = Factory(server.Options);
        await using var first = await factory.OpenAsync(Request(slot: 0, channel: "One"), CancellationToken.None);
        await using var second = await factory.OpenAsync(Request(slot: 1, channel: "Two"), CancellationToken.None);
        await server.ReceiveAsync(frame => frame.Kind == FakeDescramblingServer.FrameKind.CaPmt);
        await server.ReceiveAsync(frame => frame.Kind == FakeDescramblingServer.FrameKind.CaPmt);
        Assert.Equal(1, server.Connections);

        await server.SendAsync(server.SetFilter(23, 0, 0, Ts.EcmPid, 0x80, 0xFE));
        await WaitUntilAsync(() => first.RequiredPids.Count == 1);

        var problems = new List<string?>();
        var diagnostics = (IServiceDescramblerDiagnostics)first;
        diagnostics.StatusChanged += (_, _) =>
        {
            lock (problems)
            {
                problems.Add(diagnostics.Status.Problem?.Code);
            }
        };

        server.DropClient();

        // The server forgot the filters with the connection, and gets both services again.
        await server.WaitForConnectionAsync(2);
        var resent = new[]
        {
            await server.ReceiveAsync(frame => frame.Kind == FakeDescramblingServer.FrameKind.CaPmt),
            await server.ReceiveAsync(frame => frame.Kind == FakeDescramblingServer.FrameKind.CaPmt),
        };
        Assert.All(resent, frame => Assert.Equal(DvbApiProtocol.ListManagementAdd, frame.Body[0]));
        Assert.Equal([22, 23], resent.Select(frame => (int)frame.Body[19]).Order());
        Assert.Empty(first.RequiredPids);

        // Connected flips before the change that clears the problem is announced, so it is that
        // announcement that is waited for, not the flag.
        await WaitUntilAsync(() => ((IServiceDescramblerDiagnostics)first).Status.Connected);
        await WaitUntilAsync(() =>
        {
            lock (problems)
            {
                return problems.Count > 0 && problems[^1] is null;
            }
        });
        lock (problems)
        {
            Assert.Contains(DescramblerErrors.ConnectionLostCode, problems);
        }
    }

    [Fact]
    public async Task Closing_a_service_stops_it_in_the_server_and_the_last_one_closes_the_connection()
    {
        await using var server = new FakeDescramblingServer();
        await using var factory = Factory(server.Options);
        var first = await factory.OpenAsync(Request(slot: 0), CancellationToken.None);
        var second = await factory.OpenAsync(Request(slot: 5), CancellationToken.None);

        await first.DisposeAsync();
        var stop = await server.ReceiveAsync(frame => frame.Kind == FakeDescramblingServer.FrameKind.CaStop);
        Assert.Equal(Convert.FromHexString("9F803F0483020017"), stop.Body);

        await second.DisposeAsync();
        stop = await server.ReceiveAsync(frame => frame.Kind == FakeDescramblingServer.FrameKind.CaStop);
        Assert.Equal(Convert.FromHexString("9F803F0483020012"), stop.Body);

        // A new service after that opens a new connection.
        await using var third = await factory.OpenAsync(Request(slot: 0), CancellationToken.None);
        Assert.Equal(2, server.Connections);
    }

    [Fact]
    public async Task A_program_map_change_is_sent_as_an_update()
    {
        await using var server = new FakeDescramblingServer();
        await using var factory = Factory(server.Options);
        await using var descrambler = await factory.OpenAsync(Request(), CancellationToken.None);
        await server.ReceiveAsync(frame => frame.Kind == FakeDescramblingServer.FrameKind.CaPmt);

        await descrambler.UpdateProgramMapAsync(Ts.ProgramMap(version: 2, withAudio: false), CancellationToken.None);

        var update = await server.ReceiveAsync(frame => frame.Kind == FakeDescramblingServer.FrameKind.CaPmt);
        Assert.Equal(DvbApiProtocol.ListManagementUpdate, update.Body[0]);
        Assert.Equal(0xC0 | (2 << 1) | 1, update.Body[3]);
    }

    [Fact]
    public async Task No_control_word_in_time_is_reported_with_what_was_seen()
    {
        await using var server = new FakeDescramblingServer();
        await using var factory = Factory(server.Options);
        await using var descrambler = await factory.OpenAsync(Request(channel: "Sky Sport"), CancellationToken.None);
        await server.SendAsync(server.SetFilter(21, 0, 0, Ts.EcmPid, 0x80, 0xFE));
        await WaitUntilAsync(() => descrambler.RequiredPids.Count == 1);
        var continuity = 0;
        descrambler.Process(Ts.Packets(Ts.EcmPid, ref continuity, Ts.Ecm(0x80, 1)));

        var diagnostics = (IServiceDescramblerDiagnostics)descrambler;
        await WaitUntilAsync(() => diagnostics.Status.Problem is not null);

        var problem = diagnostics.Status.Problem!;
        Assert.Equal(DescramblerErrors.NoControlWordCode, problem.Code);
        Assert.Equal(DescramblerErrors.ProviderId, problem.ProviderId);
        Assert.Contains("Sky Sport", problem.Message, StringComparison.Ordinal);
        Assert.Contains($"127.0.0.1:{server.Port}", problem.Message, StringComparison.Ordinal);
        Assert.Contains("1 ECMs were forwarded", problem.Message, StringComparison.Ordinal);
        Assert.Contains("entitled", problem.Remedy, StringComparison.Ordinal);
    }

    /// <summary>
    /// The server answers the ECM (ECM_INFO) but keeps the key, as it does when another client holds
    /// the stream pids on the same ca device: the report says so and names the device.
    /// </summary>
    [Fact]
    public async Task An_answer_without_a_key_is_reported_with_the_ca_device()
    {
        await using var server = new FakeDescramblingServer();
        await using var factory = Factory(server.Options);
        await using var descrambler = await factory.OpenAsync(Request(slot: 2, channel: "Sky Sport Bundesliga 2"), CancellationToken.None);
        await server.SendAsync(server.SetFilter(21, 0, 0, Ts.EcmPid, 0x80, 0xFE));
        await WaitUntilAsync(() => descrambler.RequiredPids.Count == 1);
        var continuity = 0;
        descrambler.Process(Ts.Packets(Ts.EcmPid, ref continuity, Ts.Ecm(0x80, 1)));
        await server.SendAsync(server.EcmInfo(21, 0x098D, Ts.EcmPid, "Cache"));

        var diagnostics = (IServiceDescramblerDiagnostics)descrambler;
        await WaitUntilAsync(() => diagnostics.Status.Problem is not null);

        var problem = diagnostics.Status.Problem!;
        Assert.Equal(DescramblerErrors.NoControlWordCode, problem.Code);
        Assert.Equal("Cache", diagnostics.Status.LastEcm!.Reader);
        Assert.Contains("handed over no control word", problem.Message, StringComparison.Ordinal);
        Assert.Contains("ca device 21", problem.Message, StringComparison.Ordinal);
        Assert.Contains("another dvbapi client descrambles the same channel", problem.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A real descrambling server reads a new connection with the protocol version of the last
    /// client that greeted; after a version 3 client it ignores a plain greeting. The client then
    /// greets again with the version 3 frame, and the server is back at version 2 for the next one.
    /// </summary>
    [Fact]
    public async Task A_server_left_at_version_three_by_another_client_is_greeted_framed()
    {
        await using var server = new FakeDescramblingServer(listeningVersion: 3);
        await using var factory = Factory(server.Options);

        await using var descrambler = await factory.OpenAsync(Request(), CancellationToken.None);

        Assert.Equal(1, server.IgnoredGreetings);
        Assert.Equal(2, server.Connections);
        Assert.Equal(2, server.ListeningVersion);
        var caPmt = await server.ReceiveAsync(frame => frame.Kind == FakeDescramblingServer.FrameKind.CaPmt);
        Assert.Equal(DvbApiProtocol.ListManagementAdd, caPmt.Body[0]);
    }

    [Fact]
    public async Task A_descrambling_server_that_is_not_there_is_named_in_the_error()
    {
        int port;
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        await using var factory = Factory(new DvbApiOptions { Host = "127.0.0.1", Port = port });

        var exception = await Assert.ThrowsAsync<TunerException>(() => factory.OpenAsync(Request(), CancellationToken.None));

        Assert.Equal(DescramblerErrors.UnreachableCode, exception.Code);
        Assert.Equal(DescramblerErrors.ProviderId, exception.ProviderId);
        Assert.Contains($"127.0.0.1:{port}", exception.Message, StringComparison.Ordinal);
        Assert.Contains("dvbapi clients on the port set here", exception.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_port_that_does_not_answer_the_greeting_is_not_taken_for_a_descrambling_server()
    {
        await using var silent = new FakeDescramblingServer(answerGreeting: false);
        await using var factory = Factory(silent.Options);

        var exception = await Assert.ThrowsAsync<TunerException>(() => factory.OpenAsync(Request(), CancellationToken.None));

        Assert.Equal(DescramblerErrors.HandshakeFailedCode, exception.Code);
        Assert.Contains("web interface", exception.Remedy, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_options_nothing_is_configured_and_opening_says_so()
    {
        await using var factory = Factory(null);

        Assert.False(factory.IsConfigured);
        var exception = await Assert.ThrowsAsync<TunerException>(() => factory.OpenAsync(Request(channel: "Sky One"), CancellationToken.None));
        Assert.Equal(DescramblerErrors.NotConfiguredCode, exception.Code);
        Assert.Contains("Sky One", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_options_it_is_configured()
    {
        await using var factory = Factory(new DvbApiOptions { Host = "descrambler.local", Port = 9000 });

        Assert.True(factory.IsConfigured);
    }

    [Fact]
    public async Task More_services_than_the_server_can_take_are_refused()
    {
        await using var server = new FakeDescramblingServer();
        await using var factory = Factory(server.Options);

        var exception = await Assert.ThrowsAsync<TunerException>(() => factory.OpenAsync(Request(slot: 32), CancellationToken.None));

        Assert.Equal(DescramblerErrors.TooManyServicesCode, exception.Code);
    }
}
