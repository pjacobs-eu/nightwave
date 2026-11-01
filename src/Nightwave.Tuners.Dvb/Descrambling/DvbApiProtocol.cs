using System.Buffers.Binary;
using System.Text;

namespace Nightwave.Tuners.Dvb.Descrambling;

/// <summary>
/// The network dvbapi protocol the descrambling server speaks on its <c>[dvbapi] listen_port</c>:
/// message layouts, how they are framed, and how to read and write them.
/// </summary>
/// <remarks>
/// <para>
/// Checked against the reference implementation's module-dvbapi.c and module-dvbapi.h
/// (<c>dvbapi_net_send</c>, <c>dvbapi_handlesockmsg</c>, <c>dvbapi_get_packet_size</c>) and its
/// README.dvbapi_protocol, at commit 5905109e (July 2026).
/// </para>
/// <para>
/// All integers are big-endian (protocol version 1 and later). Every message starts with a 32 bit
/// opcode. From protocol version 3 on, every message after the handshake - both directions - is
/// preceded by a start byte 0xA5 and a 32 bit message id; the server repeats the id of the
/// request a reply belongs to. Version 2 has no such prefix. The client's greeting is never
/// prefixed, because the server reads it before it knows the version; the server's answer is
/// prefixed if the client announced 3. (The server itself does not always keep to that, which is
/// why <see cref="DvbApiConnection"/> can also greet with the prefix.)
/// </para>
/// <para>Server to client, after the opcode:</para>
/// <code>
/// SERVER_INFO        0xFFFF0002  u16 version, u8 n, n bytes, the server's name and version
/// DMX_SET_FILTER     0x403C6F2B  u8 adapter, u8 demux, u8 filter, u16 pid,
///                                u8[16] filter, u8[16] mask, u8[16] mode, u32 timeout, u32 flags
/// DMX_STOP           0x00006F2A  u8 adapter, u8 demux, u8 filter, u16 pid
/// CA_SET_PID         0x40086F87  u8 adapter, u32 pid, s32 index (-1: pid no longer descrambled)
/// CA_SET_DESCR       0x40106F86  u8 adapter, u32 index, u32 parity (0 even, 1 odd), u8[8] cw
/// CA_SET_DESCR_MODE  0x400C6F88  u8 adapter, u32 index, u32 algo (0 CSA, 1 DES, 2 AES-128),
///                                u32 cipher mode (0 ECB, 1 CBC)
/// CA_SET_DESCR_DATA  0x40186F89  u8 adapter, u32 index, u32 parity, u32 type (0 IV, 1 key),
///                                u32 n, n bytes
/// CA_GET_DESCR_INFO  0x80086F83  u8 adapter, u32 count, u32 type (defined, never sent)
/// ECM_INFO           0xFFFF0003  u8 adapter, u16 service id, u16 caid, u16 pid, u32 provider,
///                                u32 ecm time ms, 4 x (u8 n, n bytes): card system, reader,
///                                source, protocol; u8 hops
/// </code>
/// <para>
/// The "demux" in DMX_SET_FILTER and DMX_STOP is the server's own demuxer number, which
/// FILTER_DATA has to repeat; the "adapter" is the adapter index this client put into the CA PMT,
/// which is how the messages of one connection find their service.
/// </para>
/// <para>Client to server:</para>
/// <code>
/// CLIENT_INFO        0xFFFF0001  u16 version, u8 n, n bytes client name
/// CA_PMT             9F 80 32    ASN.1 length, EN 50221 ca_pmt (see CaProgramMap)
/// CA_STOP            9F 80 3F 04 83 02 00 demux (0xFF: every demux of this connection)
/// FILTER_DATA        0xFFFF0000  u8 demux, u8 filter, the whole section
/// </code>
/// </remarks>
internal static class DvbApiProtocol
{
    public const uint FilterData = 0xFFFF0000;
    public const uint ClientInfo = 0xFFFF0001;
    public const uint ServerInfo = 0xFFFF0002;
    public const uint EcmInfo = 0xFFFF0003;
    public const uint DmxSetFilter = 0x403C6F2B;
    public const uint DmxStop = 0x00006F2A;
    public const uint CaSetPid = 0x40086F87;
    public const uint CaSetDescr = 0x40106F86;
    public const uint CaSetDescrMode = 0x400C6F88;
    public const uint CaSetDescrData = 0x40186F89;
    public const uint CaGetDescrInfo = 0x80086F83;

    /// <summary>The version 3 message prefix: this byte, then a 32 bit message id.</summary>
    public const byte MessageStart = 0xA5;

    /// <summary>
    /// What this client announces unless told otherwise. Version 2 has everything descrambling
    /// needs (ECM_INFO came with it) and keeps the frames free of the message ids the reference
    /// implementation's module-dvbapi.c adds from version 3 on; version 3 is implemented and
    /// tested, but gains nothing here.
    /// </summary>
    public const int DefaultProtocolVersion = 2;

    // ca_pmt_list_management values, EN 50221 8.4.3.4.
    public const byte ListManagementAdd = 0x04;
    public const byte ListManagementUpdate = 0x05;

    public const byte CaPmtCommandOkDescrambling = 0x01;

    // Linux DVB section filter flags, as the server passes them on.
    public const uint FilterFlagCheckCrc = 1;
    public const uint FilterFlagOneShot = 2;

    public const int FilterLength = 16;

    private static int PrefixLength(int protocolVersion) => protocolVersion >= 3 ? 5 : 0;

    // -------------------------------------------------------------------------------------------
    // Client to server
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The greeting. Plain as the protocol has it, or <paramref name="framed"/> with the version
    /// 3 prefix and message id 0 for a server that reads new connections as version 3 (see
    /// <see cref="DvbApiConnection"/>).
    /// </summary>
    public static byte[] EncodeClientInfo(int protocolVersion, string clientName, bool framed = false)
    {
        var name = TruncatedUtf8(clientName);
        var prefix = framed ? 5 : 0;
        var message = new byte[prefix + 4 + 2 + 1 + name.Length];
        if (framed)
        {
            message[0] = MessageStart;
        }

        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(prefix), ClientInfo);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(prefix + 4), (ushort)protocolVersion);
        message[prefix + 6] = (byte)name.Length;
        name.CopyTo(message, prefix + 7);
        return message;
    }

    /// <summary>A CA PMT object: the tag, the ASN.1 length and the ca_pmt body.</summary>
    public static byte[] EncodeCaPmt(int protocolVersion, uint messageId, ReadOnlySpan<byte> body)
    {
        Span<byte> length = stackalloc byte[3];
        int lengthBytes;
        if (body.Length < 0x80)
        {
            length[0] = (byte)body.Length;
            lengthBytes = 1;
        }
        else if (body.Length <= 0xFF)
        {
            length[0] = 0x81;
            length[1] = (byte)body.Length;
            lengthBytes = 2;
        }
        else
        {
            length[0] = 0x82;
            BinaryPrimitives.WriteUInt16BigEndian(length[1..], (ushort)body.Length);
            lengthBytes = 3;
        }

        var prefix = PrefixLength(protocolVersion);
        var message = new byte[prefix + 3 + lengthBytes + body.Length];
        WritePrefix(protocolVersion, messageId, message);
        message[prefix] = 0x9F;
        message[prefix + 1] = 0x80;
        message[prefix + 2] = 0x32;
        length[..lengthBytes].CopyTo(message.AsSpan(prefix + 3));
        body.CopyTo(message.AsSpan(prefix + 3 + lengthBytes));
        return message;
    }

    /// <summary>Stops the service the server knows under this demux index (the CA PMT's descriptor 0x86).</summary>
    public static byte[] EncodeCaStop(int protocolVersion, uint messageId, int demuxIndex)
    {
        var prefix = PrefixLength(protocolVersion);
        var message = new byte[prefix + 8];
        WritePrefix(protocolVersion, messageId, message);
        ReadOnlySpan<byte> stop = [0x9F, 0x80, 0x3F, 0x04, 0x83, 0x02, 0x00];
        stop.CopyTo(message.AsSpan(prefix));
        message[prefix + 7] = (byte)demuxIndex;
        return message;
    }

    public static byte[] EncodeFilterData(int protocolVersion, uint messageId, int demux, int filter, ReadOnlySpan<byte> section)
    {
        var prefix = PrefixLength(protocolVersion);
        var message = new byte[prefix + 6 + section.Length];
        WritePrefix(protocolVersion, messageId, message);
        BinaryPrimitives.WriteUInt32BigEndian(message.AsSpan(prefix), FilterData);
        message[prefix + 4] = (byte)demux;
        message[prefix + 5] = (byte)filter;
        section.CopyTo(message.AsSpan(prefix + 6));
        return message;
    }

    private static void WritePrefix(int protocolVersion, uint messageId, Span<byte> message)
    {
        if (protocolVersion >= 3)
        {
            message[0] = MessageStart;
            BinaryPrimitives.WriteUInt32BigEndian(message[1..], messageId);
        }
    }

    private static byte[] TruncatedUtf8(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return bytes.Length <= 255 ? bytes : bytes[..255];
    }

    // -------------------------------------------------------------------------------------------
    // Server to client
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Reads one message from the start of <paramref name="buffer"/>. Returns false and sets
    /// <paramref name="consumed"/> to 0 when the buffer does not hold a whole message yet.
    /// </summary>
    /// <exception cref="DvbApiProtocolException">
    /// The bytes are not a message this protocol has: the stream cannot be read any further.
    /// </exception>
    public static bool TryDecode(
        ReadOnlySpan<byte> buffer,
        int protocolVersion,
        out DvbApiServerMessage? message,
        out int consumed)
    {
        message = null;
        consumed = 0;

        var prefix = PrefixLength(protocolVersion);
        if (buffer.Length < prefix + 4)
        {
            return false;
        }

        uint messageId = 0;
        if (prefix > 0)
        {
            if (buffer[0] != MessageStart)
            {
                throw new DvbApiProtocolException(
                    $"Expected a message start byte 0x{MessageStart:X2} (protocol version 3), got 0x{buffer[0]:X2}.");
            }

            messageId = BinaryPrimitives.ReadUInt32BigEndian(buffer[1..]);
        }

        var body = buffer[prefix..];
        var opcode = BinaryPrimitives.ReadUInt32BigEndian(body);
        var reader = new FieldReader(body[4..]);

        switch (opcode)
        {
            case ServerInfo:
            {
                if (!reader.TryU16(out var version) || !reader.TryString(out var text))
                {
                    return false;
                }

                message = new DvbApiServerInfo(version, text);
                break;
            }

            case DmxSetFilter:
            {
                if (!reader.TryU8(out var adapter) || !reader.TryU8(out var demux) || !reader.TryU8(out var filter)
                    || !reader.TryU16(out var pid) || !reader.TryBytes(FilterLength, out var data)
                    || !reader.TryBytes(FilterLength, out var mask) || !reader.TryBytes(FilterLength, out var mode)
                    || !reader.TryU32(out var timeout) || !reader.TryU32(out var flags))
                {
                    return false;
                }

                message = new DvbApiSetFilter(adapter, demux, filter, pid & 0x1FFF, data, mask, mode, timeout, flags);
                break;
            }

            case DmxStop:
            {
                if (!reader.TryU8(out var adapter) || !reader.TryU8(out var demux) || !reader.TryU8(out var filter)
                    || !reader.TryU16(out var pid))
                {
                    return false;
                }

                message = new DvbApiStopFilter(adapter, demux, filter, pid & 0x1FFF);
                break;
            }

            case CaSetPid:
            {
                if (!reader.TryU8(out var adapter) || !reader.TryU32(out var pid) || !reader.TryU32(out var index))
                {
                    return false;
                }

                message = new DvbApiSetPid(adapter, (int)(pid & 0x1FFF), unchecked((int)index));
                break;
            }

            case CaSetDescr:
            {
                if (!reader.TryU8(out var adapter) || !reader.TryU32(out var index) || !reader.TryU32(out var parity)
                    || !reader.TryBytes(8, out var controlWord))
                {
                    return false;
                }

                message = new DvbApiSetDescrambler(adapter, (int)index, (int)parity, controlWord);
                break;
            }

            case CaSetDescrMode:
            {
                if (!reader.TryU8(out var adapter) || !reader.TryU32(out var index) || !reader.TryU32(out var algorithm)
                    || !reader.TryU32(out var cipherMode))
                {
                    return false;
                }

                message = new DvbApiSetDescramblerMode(adapter, (int)index, (int)algorithm, (int)cipherMode);
                break;
            }

            case CaSetDescrData:
            {
                if (!reader.TryU8(out var adapter) || !reader.TryU32(out var index) || !reader.TryU32(out var parity)
                    || !reader.TryU32(out var dataType) || !reader.TryU32(out var length))
                {
                    return false;
                }

                if (length > 64)
                {
                    throw new DvbApiProtocolException($"CA_SET_DESCR_DATA announces {length} bytes; keys and IVs are at most 64.");
                }

                if (!reader.TryBytes((int)length, out var data))
                {
                    return false;
                }

                message = new DvbApiSetDescramblerData(adapter, (int)index, (int)parity, (int)dataType, data);
                break;
            }

            case CaGetDescrInfo:
            {
                if (!reader.TryU8(out var adapter) || !reader.TryU32(out _) || !reader.TryU32(out _))
                {
                    return false;
                }

                message = new DvbApiIgnored(adapter, opcode);
                break;
            }

            case EcmInfo:
            {
                if (!reader.TryU8(out var adapter) || !reader.TryU16(out var serviceId) || !reader.TryU16(out var caid)
                    || !reader.TryU16(out var pid) || !reader.TryU32(out var provider) || !reader.TryU32(out var ecmTime)
                    || !reader.TryString(out var cardSystem) || !reader.TryString(out var readerName)
                    || !reader.TryString(out var source) || !reader.TryString(out var protocol)
                    || !reader.TryU8(out var hops))
                {
                    return false;
                }

                message = new DvbApiEcmInfo(adapter, serviceId, caid, pid, provider, ecmTime, cardSystem, readerName, source, protocol, hops);
                break;
            }

            default:
                throw new DvbApiProtocolException(
                    $"Unknown dvbapi opcode 0x{opcode:X8}; its length is unknown, so nothing after it can be read.");
        }

        message = message with { MessageId = messageId };
        consumed = prefix + 4 + reader.Position;
        return true;
    }

    /// <summary>Reads big-endian fields and says so when the buffer ends first.</summary>
    private ref struct FieldReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;

        public int Position { get; private set; }

        public bool TryU8(out int value)
        {
            value = 0;
            if (_data.Length - Position < 1)
            {
                return false;
            }

            value = _data[Position++];
            return true;
        }

        public bool TryU16(out int value)
        {
            value = 0;
            if (_data.Length - Position < 2)
            {
                return false;
            }

            value = BinaryPrimitives.ReadUInt16BigEndian(_data[Position..]);
            Position += 2;
            return true;
        }

        public bool TryU32(out uint value)
        {
            value = 0;
            if (_data.Length - Position < 4)
            {
                return false;
            }

            value = BinaryPrimitives.ReadUInt32BigEndian(_data[Position..]);
            Position += 4;
            return true;
        }

        public bool TryBytes(int count, out byte[] value)
        {
            value = [];
            if (_data.Length - Position < count)
            {
                return false;
            }

            value = _data.Slice(Position, count).ToArray();
            Position += count;
            return true;
        }

        public bool TryString(out string value)
        {
            value = string.Empty;
            if (!TryU8(out var length))
            {
                return false;
            }

            if (!TryBytes(length, out var bytes))
            {
                return false;
            }

            value = Encoding.UTF8.GetString(bytes);
            return true;
        }
    }
}

/// <summary>The byte stream from the server is not the dvbapi protocol, or not a version of it this client reads.</summary>
internal sealed class DvbApiProtocolException(string message) : Exception(message);

/// <summary>A message from the descrambling server. <see cref="Adapter"/> is -1 for those that concern the connection.</summary>
internal abstract record DvbApiServerMessage(int Adapter)
{
    /// <summary>The version 3 message id, 0 before version 3.</summary>
    public uint MessageId { get; init; }
}

internal sealed record DvbApiServerInfo(int ProtocolVersion, string ServerVersion) : DvbApiServerMessage(-1);

internal sealed record DvbApiSetFilter(
    int Adapter,
    int Demux,
    int Filter,
    int Pid,
    byte[] Data,
    byte[] Mask,
    byte[] Mode,
    uint Timeout,
    uint Flags) : DvbApiServerMessage(Adapter);

internal sealed record DvbApiStopFilter(int Adapter, int Demux, int Filter, int Pid) : DvbApiServerMessage(Adapter);

/// <summary><see cref="Index"/> is -1 when the pid is no longer to be descrambled.</summary>
internal sealed record DvbApiSetPid(int Adapter, int Pid, int Index) : DvbApiServerMessage(Adapter);

/// <summary>An 8 byte control word. <see cref="Parity"/> 0 is even, 1 odd.</summary>
internal sealed record DvbApiSetDescrambler(int Adapter, int Index, int Parity, byte[] ControlWord)
    : DvbApiServerMessage(Adapter);

internal sealed record DvbApiSetDescramblerMode(int Adapter, int Index, int Algorithm, int CipherMode)
    : DvbApiServerMessage(Adapter)
{
    public const int AlgorithmCsa = 0;
    public const int AlgorithmDes = 1;
    public const int AlgorithmAes128 = 2;

    /// <summary>
    /// The server's <c>CW_ALGO_CSA_ALT</c>: DVB-CSA with the iCAM key schedule. The server sends
    /// it instead of plain CSA when its own iCAM test matches the ECM the control word came from
    /// (VideoGuard, ECM byte 4 not zero, byte 2 four more than byte 4), and only with
    /// <c>extended_cw_api</c> on. The cipher mode that comes with it means nothing: the server
    /// sets CBC on every control word its readers return.
    /// </summary>
    public const int AlgorithmCsaAlt = 3;

    public const int ModeEcb = 0;
    public const int ModeCbc = 1;
}

/// <summary>A key or initialisation vector longer than CA_SET_DESCR carries (AES-128).</summary>
internal sealed record DvbApiSetDescramblerData(int Adapter, int Index, int Parity, int DataType, byte[] Data)
    : DvbApiServerMessage(Adapter)
{
    public const int TypeIv = 0;
    public const int TypeKey = 1;
}

internal sealed record DvbApiEcmInfo(
    int Adapter,
    int ServiceId,
    int CaId,
    int Pid,
    uint ProviderId,
    uint EcmTimeMilliseconds,
    string CardSystem,
    string Reader,
    string Source,
    string Protocol,
    int Hops) : DvbApiServerMessage(Adapter);

/// <summary>A message that is read to keep the stream in step and otherwise has no use here.</summary>
internal sealed record DvbApiIgnored(int Adapter, uint Opcode) : DvbApiServerMessage(Adapter);
