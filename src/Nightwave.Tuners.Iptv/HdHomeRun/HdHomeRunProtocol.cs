using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nightwave.Tuners.Iptv.HdHomeRun;

/// <summary>A HDHomeRun's answer to a search.</summary>
/// <param name="DeviceId">Its id, in hexadecimal.</param>
/// <param name="BaseUrl">Where its HTTP interface is; older firmware does not say.</param>
public sealed record HdHomeRunAnswer(string DeviceId, Uri? BaseUrl);

/// <summary>
/// The search packets of a HDHomeRun, sent to UDP port 65001: a type and a length, big endian,
/// tag-length-value fields, and a little endian CRC-32 over all of it.
/// </summary>
public static class HdHomeRunProtocol
{
    public const int Port = 65001;

    private const ushort DiscoverRequest = 0x0002;
    private const ushort DiscoverReply = 0x0003;
    private const byte TagDeviceType = 0x01;
    private const byte TagDeviceId = 0x02;
    private const byte TagTunerCount = 0x10;
    private const byte TagLineupUrl = 0x27;
    private const byte TagBaseUrl = 0x2A;
    private const uint DeviceTypeTuner = 1;
    private const uint Wildcard = 0xFFFFFFFF;
    private static readonly byte[] IdLookup = [0xA, 0x5, 0xF, 0x6, 0x7, 0xC, 0x1, 0xB, 0x9, 0x2, 0x8, 0xD, 0x4, 0x3, 0xE, 0x0];

    /// <summary>Asks every tuner that hears it to answer.</summary>
    public static byte[] Request()
    {
        var packet = new byte[4 + 6 + 4];

        BinaryPrimitives.WriteUInt16BigEndian(packet, DiscoverRequest);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 6);
        packet[4] = TagDeviceType;
        packet[5] = 4;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(6), DeviceTypeTuner);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(10), Crc32(packet.AsSpan(0, 10)));

        return packet;
    }

    /// <summary>Reads an answer. Null for anything that is not an intact one.</summary>
    public static HdHomeRunAnswer? Read(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 8)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);

        if (BinaryPrimitives.ReadUInt16BigEndian(packet) != DiscoverReply
            || packet.Length < 4 + length + 4
            || BinaryPrimitives.ReadUInt32LittleEndian(packet[(4 + length)..]) != Crc32(packet[..(4 + length)]))
        {
            return null;
        }

        var fields = packet.Slice(4, length);
        string? id = null;
        Uri? baseUrl = null;

        while (fields.Length >= 2)
        {
            var tag = fields[0];
            var size = fields[1] & 0x7F;
            var header = 2;

            // A length of 128 and more takes a second byte.
            if ((fields[1] & 0x80) != 0)
            {
                if (fields.Length < 3)
                {
                    break;
                }

                size |= fields[2] << 7;
                header = 3;
            }

            if (fields.Length < header + size)
            {
                break;
            }

            var value = fields.Slice(header, size);

            if (tag == TagDeviceId && size == 4)
            {
                id = Convert.ToHexString(value);
            }
            else if (tag == TagBaseUrl)
            {
                _ = Uri.TryCreate(Encoding.UTF8.GetString(value), UriKind.Absolute, out baseUrl);
            }

            fields = fields[(header + size)..];
        }

        return id is null ? null : new HdHomeRunAnswer(id, baseUrl);
    }

    /// <summary>
    /// A new device id, in hexadecimal. Its last digit is a checksum over the others, which the
    /// maker's own software checks.
    /// </summary>
    public static string NewDeviceId()
    {
        var id = (uint)Random.Shared.NextInt64(0x10000000, 0x100000000) & 0xFFFFFFF0;

        return ((id & 0xFFFFFFF0) | Checksum(id)).ToString("X8", CultureInfo.InvariantCulture);
    }

    /// <summary>Whether a device id carries the right checksum.</summary>
    public static bool IsValidDeviceId(string id) =>
        uint.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) && Checksum(value) == (value & 0xF);

    /// <summary>Whether a packet is an intact search that a tuner with this id should answer.</summary>
    public static bool IsSearchFor(ReadOnlySpan<byte> packet, string deviceId)
    {
        if (packet.Length < 8)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(packet[2..]);

        if (BinaryPrimitives.ReadUInt16BigEndian(packet) != DiscoverRequest
            || packet.Length < 4 + length + 4
            || BinaryPrimitives.ReadUInt32LittleEndian(packet[(4 + length)..]) != Crc32(packet[..(4 + length)]))
        {
            return false;
        }

        var id = uint.Parse(deviceId, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var fields = packet.Slice(4, length);

        while (fields.Length >= 2 && (fields[1] & 0x80) == 0 && fields.Length >= 2 + fields[1])
        {
            var value = fields.Slice(2, fields[1]);

            if (value.Length == 4)
            {
                var number = BinaryPrimitives.ReadUInt32BigEndian(value);

                if ((fields[0] == TagDeviceType && number != DeviceTypeTuner && number != Wildcard)
                    || (fields[0] == TagDeviceId && number != id && number != Wildcard))
                {
                    return false;
                }
            }

            fields = fields[(2 + value.Length)..];
        }

        return true;
    }

    /// <summary>The answer of a tuner to a search.</summary>
    public static byte[] Reply(string deviceId, int tuners, string baseUrl, string lineupUrl)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentNullException.ThrowIfNull(lineupUrl);

        var body = new List<byte>();

        void Field(byte tag, ReadOnlySpan<byte> value)
        {
            body.Add(tag);
            body.Add((byte)value.Length);
            body.AddRange(value);
        }

        var number = new byte[4];

        BinaryPrimitives.WriteUInt32BigEndian(number, DeviceTypeTuner);
        Field(TagDeviceType, number);
        BinaryPrimitives.WriteUInt32BigEndian(number, uint.Parse(deviceId, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        Field(TagDeviceId, number);
        Field(TagTunerCount, [(byte)tuners]);
        Field(TagBaseUrl, Encoding.UTF8.GetBytes(baseUrl));
        Field(TagLineupUrl, Encoding.UTF8.GetBytes(lineupUrl));

        var packet = new byte[4 + body.Count + 4];

        BinaryPrimitives.WriteUInt16BigEndian(packet, DiscoverReply);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), (ushort)body.Count);
        body.CopyTo(packet, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4 + body.Count), Crc32(packet.AsSpan(0, 4 + body.Count)));

        return packet;
    }

    /// <summary>Asks the local network, and takes what answers within <paramref name="wait"/>.</summary>
    public static async Task<IReadOnlyList<(IPAddress Address, HdHomeRunAnswer Answer)>> SearchAsync(TimeSpan wait, CancellationToken cancellationToken)
    {
        using var socket = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        deadline.CancelAfter(wait);

        await socket.SendAsync(Request(), new IPEndPoint(IPAddress.Broadcast, Port), cancellationToken).ConfigureAwait(false);

        var found = new List<(IPAddress, HdHomeRunAnswer)>();

        try
        {
            while (true)
            {
                var received = await socket.ReceiveAsync(deadline.Token).ConfigureAwait(false);

                if (Read(received.Buffer) is { } answer)
                {
                    found.Add((received.RemoteEndPoint.Address, answer));
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The time is up.
        }

        return found;
    }

    /// <summary>What the last digit of a device id has to be, given the others.</summary>
    private static uint Checksum(uint id)
    {
        uint sum = 0;

        for (var shift = 28; shift > 0; shift -= 8)
        {
            sum ^= IdLookup[(id >> shift) & 0xF];
            sum ^= (shift > 4 ? (id >> (shift - 4)) & 0xF : 0);
        }

        return sum;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var value in data)
        {
            crc ^= value;

            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
