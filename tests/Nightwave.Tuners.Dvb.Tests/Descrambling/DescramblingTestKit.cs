using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Nightwave.Tuners.Dvb.Descrambling;
using Nightwave.Tuners.Dvb.Descrambling.Csa;

namespace Nightwave.Tuners.Dvb.Tests.Descrambling;

/// <summary>
/// Transport stream pieces for the descrambling tests: program map sections, sections cut into
/// packets, scrambled packets.
/// </summary>
internal static class Ts
{
    public const int ProgramMapPid = 0x100;
    public const int VideoPid = 0x101;
    public const int AudioPid = 0x102;
    public const int EcmPid = 0x500;
    public const int ServiceId = 0x2B66;

    /// <summary>A program map section with a CA descriptor for <paramref name="caid"/> on <see cref="EcmPid"/>.</summary>
    public static byte[] ProgramMap(int caid = 0x0963, int version = 1, bool withAudio = true, bool caOnStream = false)
    {
        var body = new List<byte>
        {
            (byte)(ServiceId >> 8), ServiceId & 0xFF,
            (byte)(0xC1 | (version << 1)), 0x00, 0x00,
            0xE0 | (VideoPid >> 8), VideoPid & 0xFF,
        };

        byte[] ca = [0x09, 0x04, (byte)(caid >> 8), (byte)caid, (byte)(0xE0 | (EcmPid >> 8)), EcmPid & 0xFF];
        var programInfo = caOnStream ? [] : ca;
        body.Add((byte)(0xF0 | (programInfo.Length >> 8)));
        body.Add((byte)programInfo.Length);
        body.AddRange(programInfo);

        // Video with a stream identifier descriptor, which is not a CA descriptor.
        byte[] videoInfo = caOnStream ? [0x52, 0x01, 0x01, .. ca] : [0x52, 0x01, 0x01];
        body.AddRange([0x1B, 0xE0 | (VideoPid >> 8), VideoPid & 0xFF, 0xF0, (byte)videoInfo.Length]);
        body.AddRange(videoInfo);
        if (withAudio)
        {
            body.AddRange([0x03, 0xE0 | (AudioPid >> 8), AudioPid & 0xFF, 0xF0, 0x00]);
        }

        return Section(0x02, body.ToArray());
    }

    /// <summary>A long-form section: table id, length, body and CRC.</summary>
    public static byte[] Section(byte tableId, byte[] body)
    {
        var length = body.Length + 4;
        var section = new byte[3 + length];
        section[0] = tableId;
        section[1] = (byte)(0xB0 | (length >> 8));
        section[2] = (byte)length;
        body.CopyTo(section, 3);
        var crc = SectionCrc.Compute(section.AsSpan(0, section.Length - 4));
        BinaryPrimitives.WriteUInt32BigEndian(section.AsSpan(section.Length - 4), crc);
        return section;
    }

    /// <summary>
    /// A VideoGuard style ECM: private short-form section, table id 0x80 or 0x81. With
    /// <paramref name="icamMode"/> it has the iCAM layout: byte 4 non-zero, byte 2 four more than
    /// byte 4, and the mode at 0x15.
    /// </summary>
    public static byte[] Ecm(byte tableId, byte serial, byte icamMode = 0)
    {
        var section = new byte[0x40];
        section[0] = tableId;
        section[1] = 0x70;
        section[2] = (byte)(section.Length - 3);
        for (var i = 3; i < section.Length; i++)
        {
            section[i] = (byte)(serial + i);
        }

        if (icamMode != 0)
        {
            section[4] = (byte)(section[2] - 4);
            section[0x15] = icamMode;
        }
        else
        {
            section[4] = 0;
        }

        return section;
    }

    /// <summary>Cuts sections into packets on <paramref name="pid"/>, each section starting a packet.</summary>
    public static byte[] Packets(int pid, ref int continuity, params byte[][] sections)
    {
        var packets = new List<byte>();
        foreach (var section in sections)
        {
            var offset = 0;
            var first = true;
            while (offset < section.Length || first)
            {
                var packet = new byte[188];
                Array.Fill(packet, (byte)0xFF);
                packet[0] = 0x47;
                packet[1] = (byte)((first ? 0x40 : 0x00) | (pid >> 8));
                packet[2] = (byte)pid;
                packet[3] = (byte)(0x10 | (continuity++ & 0x0F));
                var position = 4;
                if (first)
                {
                    packet[position++] = 0; // pointer field
                }

                var take = Math.Min(188 - position, section.Length - offset);
                section.AsSpan(offset, take).CopyTo(packet.AsSpan(position));
                offset += take;
                first = false;
                packets.AddRange(packet);
            }
        }

        return [.. packets];
    }

    /// <summary>
    /// A packet on <paramref name="pid"/> with a known clear payload, scrambled with
    /// <paramref name="key"/> and flagged even or odd. <paramref name="adaptationLength"/> moves
    /// the payload back behind an adaptation field of that many bytes (plus its length byte).
    /// </summary>
    public static (byte[] Scrambled, byte[] Clear) ScrambledPacket(
        int pid,
        CsaKey key,
        bool odd,
        int continuity,
        int adaptationLength = -1,
        int seed = 0)
    {
        var clear = new byte[188];
        clear[0] = 0x47;
        clear[1] = (byte)(pid >> 8);
        clear[2] = (byte)pid;
        var payloadStart = 4;
        if (adaptationLength >= 0)
        {
            clear[3] = (byte)(0x30 | (continuity & 0x0F));
            clear[4] = (byte)adaptationLength;
            if (adaptationLength > 0)
            {
                clear[5] = 0x00;
                clear.AsSpan(6, adaptationLength - 1).Fill(0xFF);
            }

            payloadStart = 5 + adaptationLength;
        }
        else
        {
            clear[3] = (byte)(0x10 | (continuity & 0x0F));
        }

        var random = new Random(seed + continuity);
        for (var i = payloadStart; i < 188; i++)
        {
            clear[i] = (byte)random.Next(256);
        }

        var scrambled = clear.ToArray();
        if (payloadStart < 188)
        {
            CsaReference.Scramble(key, scrambled.AsSpan(payloadStart));
        }

        scrambled[3] |= (byte)(odd ? 0xC0 : 0x80);
        return (scrambled, clear);
    }
}

/// <summary>
/// A descrambling server as far as the network dvbapi goes, on loopback: accepts clients, answers
/// the greeting, records what they send and sends what the test tells it to. Its encoder is
/// written from the protocol description independently of the one under test.
/// </summary>
internal sealed class FakeDescramblingServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<ClientFrame> _received = Channel.CreateUnbounded<ClientFrame>();
    private readonly Task _acceptLoop;
    private readonly SemaphoreSlim _connected = new(0);
    private TcpClient? _client;
    private int _protocolVersion;

    public FakeDescramblingServer(int serverProtocolVersion = 3, bool answerGreeting = true, int listeningVersion = 0)
    {
        ServerProtocolVersion = serverProtocolVersion;
        AnswerGreeting = answerGreeting;
        ListeningVersion = listeningVersion;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptAsync();
    }

    public int Port { get; }

    public int ServerProtocolVersion { get; }

    public bool AnswerGreeting { get; }

    /// <summary>
    /// The protocol version a real server reads a new connection's first bytes with: that of
    /// the last client that greeted. At 3 it only understands a greeting with the version 3
    /// frame and ignores a plain one.
    /// </summary>
    public int ListeningVersion { get; private set; }

    public int IgnoredGreetings { get; private set; }

    public int Connections { get; private set; }

    public string? ClientName { get; private set; }

    public int ClientProtocolVersion => _protocolVersion;

    public DvbApiOptions Options => new() { Host = "127.0.0.1", Port = Port, ClientName = "Nightwave test" };

    public async Task WaitForConnectionAsync(int count)
    {
        while (Connections < count)
        {
            await _connected.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>The next frame of the given kind the client sent, skipping others.</summary>
    public async Task<ClientFrame> ReceiveAsync(Func<ClientFrame, bool> match, TimeSpan? timeout = null)
    {
        using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var frame = await _received.Reader.ReadAsync(cancel.Token);
            if (match(frame))
            {
                return frame;
            }
        }
    }

    public Task SendAsync(byte[] message)
    {
        var stream = _client!.GetStream();
        return stream.WriteAsync(message).AsTask();
    }

    /// <summary>Closes the current client connection, as a restarting server would.</summary>
    public void DropClient()
    {
        _client?.Client.Close();
        _client?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        _client?.Dispose();
        try
        {
            await _acceptLoop;
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException or IOException)
        {
            // Stopped.
        }

        _stop.Dispose();
        _connected.Dispose();
    }

    // Server to client, big-endian, framed for the client's protocol version.

    public byte[] SetFilter(int adapter, int demux, int filter, int pid, byte tableId, byte tableMask) =>
        Frame(DvbApiProtocol.DmxSetFilter, writer =>
        {
            writer.Add((byte)adapter);
            writer.Add((byte)demux);
            writer.Add((byte)filter);
            writer.AddRange([(byte)(pid >> 8), (byte)pid]);
            var data = new byte[16];
            var mask = new byte[16];
            data[0] = tableId;
            mask[0] = tableMask;
            writer.AddRange(data);
            writer.AddRange(mask);
            writer.AddRange(new byte[16]); // mode
            writer.AddRange([0, 0, 0, 0]); // timeout
            writer.AddRange([0, 0, 0, 4]); // flags: immediate start
        });

    public byte[] StopFilter(int adapter, int demux, int filter, int pid) =>
        Frame(DvbApiProtocol.DmxStop, writer =>
        {
            writer.AddRange([(byte)adapter, (byte)demux, (byte)filter, (byte)(pid >> 8), (byte)pid]);
        });

    public byte[] SetPid(int adapter, int pid, int index) =>
        Frame(DvbApiProtocol.CaSetPid, writer =>
        {
            writer.Add((byte)adapter);
            writer.AddRange(BigEndian((uint)pid));
            writer.AddRange(BigEndian(unchecked((uint)index)));
        });

    public byte[] SetDescrambler(int adapter, int index, int parity, byte[] controlWord) =>
        Frame(DvbApiProtocol.CaSetDescr, writer =>
        {
            writer.Add((byte)adapter);
            writer.AddRange(BigEndian((uint)index));
            writer.AddRange(BigEndian((uint)parity));
            writer.AddRange(controlWord);
        });

    public byte[] SetDescramblerMode(int adapter, int index, int algorithm, int mode) =>
        Frame(DvbApiProtocol.CaSetDescrMode, writer =>
        {
            writer.Add((byte)adapter);
            writer.AddRange(BigEndian((uint)index));
            writer.AddRange(BigEndian((uint)algorithm));
            writer.AddRange(BigEndian((uint)mode));
        });

    public byte[] SetDescramblerData(int adapter, int index, int parity, int type, byte[] data) =>
        Frame(DvbApiProtocol.CaSetDescrData, writer =>
        {
            writer.Add((byte)adapter);
            writer.AddRange(BigEndian((uint)index));
            writer.AddRange(BigEndian((uint)parity));
            writer.AddRange(BigEndian((uint)type));
            writer.AddRange(BigEndian((uint)data.Length));
            writer.AddRange(data);
        });

    public byte[] EcmInfo(int adapter, int caid, int pid, string reader) =>
        Frame(DvbApiProtocol.EcmInfo, writer =>
        {
            writer.Add((byte)adapter);
            writer.AddRange([(byte)(Ts.ServiceId >> 8), Ts.ServiceId & 0xFF]);
            writer.AddRange([(byte)(caid >> 8), (byte)caid]);
            writer.AddRange([(byte)(pid >> 8), (byte)pid]);
            writer.AddRange(BigEndian(0x000000));
            writer.AddRange(BigEndian(123));
            foreach (var text in new[] { "videoguard2", reader, "local", "internal" })
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                writer.Add((byte)bytes.Length);
                writer.AddRange(bytes);
            }

            writer.Add(0);
        });

    private byte[] Frame(uint opcode, Action<List<byte>> write)
    {
        var message = new List<byte>();
        if (_protocolVersion >= 3)
        {
            message.Add(0xA5);
            message.AddRange(BigEndian(7));
        }

        message.AddRange(BigEndian(opcode));
        write(message);
        return [.. message];
    }

    private static byte[] BigEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            _client = client;
            Connections++;
            _connected.Release();
            _ = ReadClientAsync(client);
        }
    }

    private async Task ReadClientAsync(TcpClient client)
    {
        var buffer = new List<byte>();
        var chunk = new byte[4096];
        var stream = client.GetStream();
        var greeted = false;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(chunk, _stop.Token);
                if (read == 0)
                {
                    return;
                }

                buffer.AddRange(chunk.AsSpan(0, read).ToArray());
                while (TryTakeFrame(buffer, ref greeted, out var frame))
                {
                    if (frame!.Kind == FrameKind.ClientInfo)
                    {
                        ClientName = Encoding.UTF8.GetString(frame.Body);
                        _protocolVersion = frame.Version;
                        if (AnswerGreeting)
                        {
                            var name = Encoding.UTF8.GetBytes("Fake descrambling server 11.0");
                            var reply = new List<byte>();
                            if (_protocolVersion >= 3)
                            {
                                reply.Add(0xA5);
                                reply.AddRange(BigEndian(0));
                            }

                            reply.AddRange(BigEndian(DvbApiProtocol.ServerInfo));
                            reply.AddRange([(byte)(ServerProtocolVersion >> 8), (byte)ServerProtocolVersion, (byte)name.Length]);
                            reply.AddRange(name);
                            await stream.WriteAsync(reply.ToArray(), _stop.Token);
                        }
                    }

                    await _received.Writer.WriteAsync(frame, _stop.Token);
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The client or the test went away.
        }
    }

    private bool TryTakeFrame(List<byte> buffer, ref bool greeted, out ClientFrame? frame)
    {
        frame = null;
        var data = buffer.ToArray().AsSpan();
        if (!greeted)
        {
            var framed = ListeningVersion >= 3 ? 5 : 0;
            if (data.Length < 4)
            {
                return false;
            }

            var readable = framed == 0
                ? BinaryPrimitives.ReadUInt32BigEndian(data) == DvbApiProtocol.ClientInfo
                : data[0] == 0xA5;
            if (!readable)
            {
                // What the server does with it: "network packet malformed", and nothing else.
                IgnoredGreetings++;
                buffer.Clear();
                return false;
            }

            if (data.Length < framed + 7 || data.Length < framed + 7 + data[framed + 6])
            {
                return false;
            }

            var greetingLength = framed + 7 + data[framed + 6];
            var version = BinaryPrimitives.ReadUInt16BigEndian(data[(framed + 4)..]);
            frame = new ClientFrame(FrameKind.ClientInfo, data.Slice(framed + 7, data[framed + 6]).ToArray(), version, 0);
            buffer.RemoveRange(0, greetingLength);
            greeted = true;
            ListeningVersion = version;
            return true;
        }

        var prefix = _protocolVersion >= 3 ? 5 : 0;
        if (data.Length < prefix + 4)
        {
            return false;
        }

        uint messageId = 0;
        if (prefix > 0)
        {
            Assert.Equal(0xA5, data[0]);
            messageId = BinaryPrimitives.ReadUInt32BigEndian(data[1..]);
        }

        var body = data[prefix..];
        int length;
        FrameKind kind;
        byte[] content;
        if (body[0] == 0x9F && body[1] == 0x80 && body[2] == 0x32)
        {
            // ASN.1 length.
            int lengthBytes;
            int contentLength;
            if ((body[3] & 0x80) == 0)
            {
                lengthBytes = 1;
                contentLength = body[3];
            }
            else
            {
                lengthBytes = 1 + (body[3] & 0x7F);
                if (body.Length < 3 + lengthBytes)
                {
                    return false;
                }

                contentLength = 0;
                for (var i = 1; i < lengthBytes; i++)
                {
                    contentLength = (contentLength << 8) | body[3 + i];
                }
            }

            length = 3 + lengthBytes + contentLength;
            if (body.Length < length)
            {
                return false;
            }

            kind = FrameKind.CaPmt;
            content = body.Slice(3 + lengthBytes, contentLength).ToArray();
        }
        else if (body[0] == 0x9F && body[1] == 0x80 && body[2] == 0x3F)
        {
            length = 8;
            if (body.Length < length)
            {
                return false;
            }

            kind = FrameKind.CaStop;
            content = body[..8].ToArray();
        }
        else
        {
            Assert.Equal(DvbApiProtocol.FilterData, BinaryPrimitives.ReadUInt32BigEndian(body));
            if (body.Length < 9)
            {
                return false;
            }

            length = 9 + (((body[7] & 0x0F) << 8) | body[8]);
            if (body.Length < length)
            {
                return false;
            }

            kind = FrameKind.FilterData;
            content = body[4..length].ToArray();
        }

        frame = new ClientFrame(kind, content, _protocolVersion, messageId);
        buffer.RemoveRange(0, prefix + length);
        return true;
    }

    internal enum FrameKind
    {
        ClientInfo,
        CaPmt,
        CaStop,
        FilterData,
    }

    /// <summary>
    /// One message from the client: for a CA PMT the ca_pmt body, for filter data demux,
    /// filter and the section, for CA_STOP the whole 8 bytes.
    /// </summary>
    internal sealed record ClientFrame(FrameKind Kind, byte[] Body, int Version, uint MessageId);
}

/// <summary>Options that come from a variable the test sets.</summary>
internal sealed class TestOptionsSource(DvbApiOptions? options) : IDvbApiOptionsSource
{
    public DvbApiOptions? Options { get; set; } = options;

    public Task<DvbApiOptions?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Options);
}
