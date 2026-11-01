using System.Net;
using System.Text;
using System.Threading.Channels;
using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.Transport;
using Nightwave.Tuners.Dvb.Tuning;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>
/// The wire formats: the SAT>IP query, RTSP responses and interleaved frames, RTP, SSDP, the
/// device description.
/// </summary>
public sealed class SatIpProtocolTests
{
    [Fact]
    public void A_dvb_s_query_carries_every_parameter_the_specification_asks_for()
    {
        var tuning = new TuningParameters
        {
            System = DvbSystem.DvbS,
            FrequencyKhz = 11836000,
            Polarization = Polarization.Horizontal,
            SymbolRate = 27500,
            Fec = "34",
        };

        Assert.Equal(
            "src=1&freq=11836&pol=h&ro=0.35&msys=dvbs&mtype=qpsk&plts=off&sr=27500&fec=34&pids=0,16,17",
            SatIpQuery.Build(tuning, 1, [17, 0, 16]));
    }

    [Fact]
    public void A_dvb_s2_query_keeps_decimals_and_switches_pilots_on_for_8psk()
    {
        var tuning = new TuningParameters
        {
            System = DvbSystem.DvbS2,
            FrequencyKhz = 10714250,
            Polarization = Polarization.Vertical,
            SymbolRate = 22000,
            Modulation = "8psk",
            RollOff = "0.20",
            Fec = "23",
        };

        Assert.Equal(
            "src=2&freq=10714.25&pol=v&ro=0.20&msys=dvbs2&mtype=8psk&plts=on&sr=22000&fec=23",
            SatIpQuery.Tuning(tuning, 2));
    }

    [Fact]
    public void Cable_and_terrestrial_queries_have_no_input()
    {
        var cable = new TuningParameters { System = DvbSystem.DvbC, FrequencyKhz = 346000, SymbolRate = 6900, Modulation = "256qam" };
        var t2 = new TuningParameters { System = DvbSystem.DvbT2, FrequencyKhz = 506000, Bandwidth = "8", Plp = 0 };

        Assert.Equal("freq=346&msys=dvbc&mtype=256qam&sr=6900&pids=none", SatIpQuery.Build(cable, null, []));
        Assert.Equal("freq=506&bw=8&msys=dvbt2&plp=0", SatIpQuery.Tuning(t2, null));
    }

    [Fact]
    public void A_transponder_from_the_nit_is_the_one_from_the_table()
    {
        var table = new TuningParameters { System = DvbSystem.DvbS, FrequencyKhz = 11836000, Polarization = Polarization.Horizontal };

        Assert.True(table.IsSameTransponder(table with { FrequencyKhz = 11835500, System = DvbSystem.DvbS2 }));
        Assert.False(table.IsSameTransponder(table with { Polarization = Polarization.Vertical }));
        Assert.False(table.IsSameTransponder(table with { FrequencyKhz = 11856000 }));
        Assert.Equal("11836 MHz H DVB-S", table.Describe());
    }

    [Fact]
    public void A_setup_response_gives_the_session_its_timeout_and_the_stream_id()
    {
        var response = RtspResponse.Parse(
            "RTSP/1.0 200 OK\r\nCSeq: 2\r\nSession: 12345678;timeout=30\r\ncom.ses.streamID: 5\r\nTransport: RTP/AVP/TCP;interleaved=0-1\r\n\r\n");

        Assert.NotNull(response);
        Assert.True(response.IsSuccess);
        Assert.Equal(2, response.CSeq);
        Assert.Equal(("12345678", TimeSpan.FromSeconds(30)), response.Session);
        Assert.Equal(5, response.StreamId);
    }

    /// <summary>
    /// A Kathrein EXIP-418 asked for interleaved RTP, as it answered on the wire: 200, headers
    /// without a space after the colon, and a UDP transport to a port it picked itself. It did not
    /// refuse, so only the transport says the stream is going somewhere else.
    /// </summary>
    [Fact]
    public void A_server_that_answers_an_interleaved_setup_with_udp_is_noticed()
    {
        var kathrein = RtspResponse.Parse(
            "RTSP/1.0 200 OK\r\nSession:8b3d30326bdab57;timeout=30\r\ncom.ses.streamID:131\r\n"
            + "Transport:RTP/AVP;unicast;destination=192.168.1.220;client_port=1500-1501\r\nCSeq:1\r\n\r\n");
        var interleaved = RtspResponse.Parse(
            "RTSP/1.0 200 OK\r\nCSeq: 2\r\nSession: 1;timeout=30\r\nTransport: RTP/AVP/TCP;interleaved=0-1\r\n\r\n");

        Assert.True(kathrein!.IsSuccess);
        Assert.Equal(("8b3d30326bdab57", TimeSpan.FromSeconds(30)), kathrein.Session);
        Assert.Equal(131, kathrein.StreamId);
        Assert.False(SatIpRtspTransport.IsInterleaved(kathrein));
        Assert.True(SatIpRtspTransport.IsInterleaved(interleaved!));
    }

    [Fact]
    public void A_session_without_a_timeout_gets_the_default_of_sixty_seconds()
    {
        var response = RtspResponse.Parse("RTSP/1.0 200 OK\r\nCSeq: 1\r\nSession: ABCDEF\r\n\r\n");

        Assert.Equal(("ABCDEF", TimeSpan.FromSeconds(60)), response!.Session);
    }

    [Fact]
    public void A_refusal_is_read_as_one()
    {
        var response = RtspResponse.Parse("RTSP/1.0 503 Service Unavailable\r\nCSeq: 3\r\n\r\n");

        Assert.False(response!.IsSuccess);
        Assert.Equal(503, response.StatusCode);
        Assert.Equal("Service Unavailable", response.Reason);
    }

    [Fact]
    public void Rtp_payload_skips_contributing_sources_extension_and_padding()
    {
        var ts = Enumerable.Range(0, 188).Select(value => (byte)value).ToArray();
        ts[0] = 0x47;

        // Version 2, padding, extension, one contributing source; payload type 33.
        var packet = new List<byte> { 0xB1, 33, 0x12, 0x34, 0, 0, 0, 1, 0, 0, 0, 2 };
        packet.AddRange([0, 0, 0, 3]);
        packet.AddRange([0xBE, 0xDE, 0x00, 0x01, 1, 2, 3, 4]);
        packet.AddRange(ts);
        packet.AddRange([0, 0, 3]);

        Assert.True(RtpPacket.TryGetPayload(packet.ToArray(), out var range, out var sequence));
        Assert.Equal(0x1234, sequence);
        Assert.Equal(ts, packet.ToArray()[range]);
    }

    [Fact]
    public void Anything_that_is_not_rtp_version_2_is_ignored()
    {
        Assert.False(RtpPacket.TryGetPayload(new byte[20], out _, out _));
        Assert.False(RtpPacket.TryGetPayload(new byte[] { 0x80, 33 }, out _, out _));
    }

    [Fact]
    public async Task Interleaved_rtp_and_responses_share_one_connection()
    {
        var wire = new FakeRtspWire();
        var connection = new RtspConnection(wire);
        var received = new List<byte[]>();
        connection.OnData = data => received.Add(data.ToArray());
        connection.Start();

        var request = connection.SendAsync("OPTIONS", "rtsp://192.0.2.1:554/", [new("Session", "1")], CancellationToken.None);

        var sent = await wire.NextRequestAsync();
        Assert.StartsWith("OPTIONS rtsp://192.0.2.1:554/ RTSP/1.0\r\nCSeq: 1\r\n", sent, StringComparison.Ordinal);
        Assert.Contains("Session: 1\r\n", sent, StringComparison.Ordinal);

        // A frame of RTP before the answer, one of RTCP, then the answer.
        byte[] rtp = [0x80, 33, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0x47, 1, 2, 3];
        wire.Send([(byte)'$', 0, 0, (byte)rtp.Length, .. rtp]);
        wire.Send([(byte)'$', 1, 0, 4, 9, 9, 9, 9]);
        wire.Send(Encoding.ASCII.GetBytes("RTSP/1.0 200 OK\r\nCSeq: 1\r\nPublic: OPTIONS, SETUP, PLAY, TEARDOWN\r\n\r\n"));

        var response = await request.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(rtp, Assert.Single(received));

        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Interleaved_rtcp_carries_the_signal_report_to_its_own_callback()
    {
        var wire = new FakeRtspWire();
        var connection = new RtspConnection(wire);
        var control = new List<byte[]>();
        var data = 0;
        connection.OnData = _ => data++;
        connection.OnControl = frame => control.Add(frame.ToArray());
        connection.Start();

        var request = connection.SendAsync("OPTIONS", "rtsp://192.0.2.1:554/", [], CancellationToken.None);
        await wire.NextRequestAsync();

        byte[] rtcp = [0x80, 204, 0, 3, 0, 0, 0, 1, (byte)'S', (byte)'E', (byte)'S', (byte)'1', 0, 0, 0, 0];
        wire.Send([(byte)'$', 1, 0, (byte)rtcp.Length, .. rtcp]);
        wire.Send(Encoding.ASCII.GetBytes("RTSP/1.0 200 OK\r\nCSeq: 1\r\n\r\n"));

        await request.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(rtcp, Assert.Single(control));
        Assert.Equal(0, data);

        await connection.DisposeAsync();
    }

    [Fact]
    public void The_capabilities_count_tuners_per_kind_of_signal()
    {
        var tuners = SatIpDeviceDescription.ParseCapabilities("DVBS2-4,DVBT2-2,DVBC-1,DVBT-1,bogus,DVBC2-x");

        Assert.Equal(4, tuners[DeliverySystem.Satellite]);
        Assert.Equal(3, tuners[DeliverySystem.Terrestrial]);
        Assert.Equal(1, tuners[DeliverySystem.Cable]);
    }

    [Fact]
    public void The_device_description_gives_name_id_and_tuners()
    {
        const string Xml = """
            <?xml version="1.0"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0">
              <specVersion><major>1</major><minor>1</minor></specVersion>
              <device>
                <deviceType>urn:ses-com:device:SatIPServer:1</deviceType>
                <friendlyName>Digibit R1</friendlyName>
                <modelName>Digibit R1</modelName>
                <UDN>uuid:11223344-5566-7788-99aa-000102030405</UDN>
                <satip:X_SATIPCAP xmlns:satip="urn:ses-com:satip">DVBS2-2</satip:X_SATIPCAP>
              </device>
            </root>
            """;

        var description = SatIpDeviceDescription.Parse(Xml);

        Assert.NotNull(description);
        Assert.Equal("Digibit R1", description.FriendlyName);
        Assert.Equal("uuid:11223344-5566-7788-99aa-000102030405", description.UniqueDeviceName);
        Assert.Equal(2, description.Tuners[DeliverySystem.Satellite]);
    }

    [Fact]
    public void An_ssdp_answer_is_read_for_its_location()
    {
        var headers = Ssdp.ParseResponse(
            "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nLOCATION: http://192.168.1.20:8080/desc.xml\r\n"
            + "ST: urn:ses-com:device:SatIPServer:1\r\nDEVICEID.SES.COM: 1\r\n\r\n");

        Assert.NotNull(headers);
        Assert.Equal("http://192.168.1.20:8080/desc.xml", headers["location"]);
        Assert.Equal(Ssdp.SatIpSearchTarget, headers["ST"]);
        Assert.Null(Ssdp.ParseResponse("NOTIFY * HTTP/1.1\r\n\r\n"));
    }

    [Fact]
    public void The_search_request_asks_for_sat_ip_servers()
    {
        var request = Encoding.ASCII.GetString(Ssdp.SearchRequest(Ssdp.SatIpSearchTarget, 2));

        Assert.StartsWith("M-SEARCH * HTTP/1.1\r\n", request, StringComparison.Ordinal);
        Assert.Contains("MAN: \"ssdp:discover\"\r\n", request, StringComparison.Ordinal);
        Assert.Contains("ST: urn:ses-com:device:SatIPServer:1\r\n", request, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\n", request, StringComparison.Ordinal);
    }

    /// <summary>
    /// The server side of an RTSP connection, in memory: what the client writes is collected,
    /// what the test sends is what the client reads.
    /// </summary>
    private sealed class FakeRtspWire : Stream
    {
        private readonly Channel<byte[]> _toClient = Channel.CreateUnbounded<byte[]>();
        private readonly Channel<string> _fromClient = Channel.CreateUnbounded<string>();
        private byte[] _current = [];
        private int _offset;

        public void Send(byte[] data) => _toClient.Writer.TryWrite(data);

        public async Task<string> NextRequestAsync() =>
            await _fromClient.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_offset >= _current.Length)
            {
                _current = await _toClient.Reader.ReadAsync(cancellationToken);
                _offset = 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;

            return count;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _fromClient.Writer.TryWrite(Encoding.ASCII.GetString(buffer.Span));

            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
