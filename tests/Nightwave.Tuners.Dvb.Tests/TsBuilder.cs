using System.Text;
using Nightwave.Tuners.Dvb.Mpeg;
using Nightwave.Tuners.Dvb.Mpeg.Psi;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>
/// Writes transport streams by hand for the tests: sections with a real CRC, packets with real
/// headers, and the descriptors of EN 300 468 byte by byte - so what is parsed is what a
/// multiplexer would send, not what the parser happens to expect.
/// </summary>
internal static class TsBuilder
{
    /// <summary>A long form section: header, body, CRC.</summary>
    public static byte[] Section(int tableId, int extension, int version, byte[] body, int number = 0, int last = 0)
    {
        var length = 5 + body.Length + 4;
        var section = new byte[3 + length];

        section[0] = (byte)tableId;
        section[1] = (byte)(0xB0 | ((length >> 8) & 0x0F));
        section[2] = (byte)length;
        section[3] = (byte)(extension >> 8);
        section[4] = (byte)extension;
        section[5] = (byte)(0xC1 | ((version & 0x1F) << 1));
        section[6] = (byte)number;
        section[7] = (byte)last;
        body.CopyTo(section, 8);
        MpegCrc32.Seal(section);

        return section;
    }

    public static byte[] Pat(int transportStreamId, int version, params (int Program, int Pid)[] programs)
    {
        var body = programs.SelectMany(program => new[]
        {
            (byte)(program.Program >> 8), (byte)program.Program,
            (byte)(0xE0 | (program.Pid >> 8)), (byte)program.Pid,
        }).ToArray();

        return Section(ProgramAssociationTable.TableId, transportStreamId, version, body);
    }

    public static byte[] Pmt(int program, int version, int pcrPid, byte[] programDescriptors, params (int Type, int Pid, byte[] Descriptors)[] streams)
    {
        var body = new List<byte>
        {
            (byte)(0xE0 | (pcrPid >> 8)), (byte)pcrPid,
            (byte)(0xF0 | (programDescriptors.Length >> 8)), (byte)programDescriptors.Length,
        };
        body.AddRange(programDescriptors);

        foreach (var (type, pid, descriptors) in streams)
        {
            body.Add((byte)type);
            body.Add((byte)(0xE0 | (pid >> 8)));
            body.Add((byte)pid);
            body.Add((byte)(0xF0 | (descriptors.Length >> 8)));
            body.Add((byte)descriptors.Length);
            body.AddRange(descriptors);
        }

        return Section(ProgramMapTable.TableId, program, version, [.. body]);
    }

    public static byte[] Sdt(int transportStreamId, int originalNetworkId, int version, params (int ServiceId, bool Scrambled, byte[] Descriptors)[] services)
    {
        var body = new List<byte> { (byte)(originalNetworkId >> 8), (byte)originalNetworkId, 0xFF };

        foreach (var (serviceId, scrambled, descriptors) in services)
        {
            body.Add((byte)(serviceId >> 8));
            body.Add((byte)serviceId);
            body.Add(0xFC);
            body.Add((byte)((4 << 5) | (scrambled ? 0x10 : 0) | (descriptors.Length >> 8)));
            body.Add((byte)descriptors.Length);
            body.AddRange(descriptors);
        }

        return Section(0x42, transportStreamId, version, [.. body]);
    }

    public static byte[] Nit(int networkId, int version, byte[] networkDescriptors, params (int TransportStreamId, int OriginalNetworkId, byte[] Descriptors)[] streams)
    {
        var loop = new List<byte>();

        foreach (var (tsid, onid, descriptors) in streams)
        {
            loop.Add((byte)(tsid >> 8));
            loop.Add((byte)tsid);
            loop.Add((byte)(onid >> 8));
            loop.Add((byte)onid);
            loop.Add((byte)(0xF0 | (descriptors.Length >> 8)));
            loop.Add((byte)descriptors.Length);
            loop.AddRange(descriptors);
        }

        var body = new List<byte>
        {
            (byte)(0xF0 | (networkDescriptors.Length >> 8)), (byte)networkDescriptors.Length,
        };
        body.AddRange(networkDescriptors);
        body.Add((byte)(0xF0 | (loop.Count >> 8)));
        body.Add((byte)loop.Count);
        body.AddRange(loop);

        return Section(0x40, networkId, version, [.. body]);
    }

    public static byte[] Descriptor(int tag, params byte[] data) => [(byte)tag, (byte)data.Length, .. data];

    /// <summary>A service descriptor with Latin names (no selector byte: ISO 6937, ASCII compatible).</summary>
    public static byte[] ServiceDescriptor(int type, string provider, string name)
    {
        var providerBytes = Encoding.ASCII.GetBytes(provider);
        var nameBytes = Encoding.ASCII.GetBytes(name);

        return Descriptor(0x48, [(byte)type, (byte)providerBytes.Length, .. providerBytes, (byte)nameBytes.Length, .. nameBytes]);
    }

    public static byte[] CaDescriptor(int systemId, int pid) =>
        Descriptor(0x09, (byte)(systemId >> 8), (byte)systemId, (byte)(0xE0 | (pid >> 8)), (byte)pid);

    /// <summary>
    /// A satellite delivery system descriptor: frequency in MHz with two decimals, position in
    /// tenths east, symbol rate in kS/s.
    /// </summary>
    public static byte[] SatelliteDelivery(decimal mhz, int positionTenths, bool horizontal, bool s2, int symbolRate, int fec)
    {
        var frequency = Bcd((long)(mhz * 100), 8);
        var position = Bcd(positionTenths, 4);
        var flags = (byte)(0x80 | (horizontal ? 0x00 : 0x20) | (s2 ? 0x04 | 0x02 : 0x01));
        var rate = Bcd(symbolRate * 10L, 7);

        // Seven digits of symbol rate and the FEC in the last nibble.
        rate[3] = (byte)((rate[3] & 0xF0) | fec);

        return Descriptor(0x43, [.. frequency, .. position, flags, .. rate]);
    }

    public static byte[] CableDelivery(decimal mhz, int symbolRate, int modulation)
    {
        var frequency = Bcd((long)(mhz * 10000), 8);
        var rate = Bcd(symbolRate * 10L, 7);
        rate[3] = (byte)((rate[3] & 0xF0) | 0x03);

        return Descriptor(0x44, [.. frequency, 0xFF, 0xF2, (byte)modulation, .. rate]);
    }

    /// <summary>
    /// Writes <paramref name="value"/> as BCD digits, left aligned: the last digit of an odd count
    /// sits in the upper half of the last byte, as the symbol rate does.
    /// </summary>
    public static byte[] Bcd(long value, int digits)
    {
        var text = value.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(digits, '0');
        if (digits % 2 == 1)
        {
            text += "0";
        }

        var bytes = new byte[text.Length / 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = (byte)(((text[2 * index] - '0') << 4) | (text[(2 * index) + 1] - '0'));
        }

        return bytes;
    }

    /// <summary>The packets of one section on one pid.</summary>
    public static List<byte[]> Packets(byte[] section, int pid, int continuity = 0)
    {
        var packets = new List<byte[]>();
        SectionPacketizer.Packetize(section, pid, continuity, packets);

        return packets;
    }

    /// <summary>A payload packet of an elementary stream, filled with <paramref name="fill"/>.</summary>
    public static byte[] EsPacket(int pid, int continuity, byte fill, int scrambling = 0, bool start = false)
    {
        var packet = new byte[TransportPacket.Size];
        TransportPacket.WriteHeader(packet, pid, start, continuity);
        packet[3] = (byte)((scrambling << 6) | 0x10 | (continuity & 0x0F));
        packet.AsSpan(4).Fill(fill);

        return packet;
    }

    /// <summary>
    /// The packet that begins a picture a decoder can start on: payload unit start, and an
    /// adaptation field whose random access indicator is set, as a broadcaster marks a keyframe.
    /// </summary>
    public static byte[] RandomAccessPacket(int pid, int continuity, byte fill, int scrambling = 0)
    {
        var packet = new byte[TransportPacket.Size];
        TransportPacket.WriteHeader(packet, pid, payloadUnitStart: true, continuity);

        // Payload and adaptation field both present.
        packet[3] = (byte)((scrambling << 6) | 0x30 | (continuity & 0x0F));

        // One byte of adaptation field, holding the flags; the random access indicator is 0x40.
        packet[4] = 1;
        packet[5] = 0x40;
        packet.AsSpan(6).Fill(fill);

        return packet;
    }

    public static byte[] Join(IEnumerable<byte[]> packets) => [.. packets.SelectMany(packet => packet)];

    /// <summary>Splits a stream into its packets, for looking at what came out.</summary>
    public static List<byte[]> Split(byte[] stream) =>
        [.. stream.Chunk(TransportPacket.Size).Where(chunk => chunk.Length == TransportPacket.Size)];
}
