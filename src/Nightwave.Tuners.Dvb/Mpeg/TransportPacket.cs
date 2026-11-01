namespace Nightwave.Tuners.Dvb.Mpeg;

/// <summary>
/// Reads the four byte header of an MPEG transport stream packet (ISO/IEC 13818-1, 2.4.3.2).
/// Everything here works on a span that starts at a sync byte and is at least one packet long;
/// checking that is the aligner's job, not every reader's.
/// </summary>
internal static class TransportPacket
{
    public const int Size = 188;

    public const byte SyncByte = 0x47;

    /// <summary>The null packet id: stuffing a multiplexer adds to keep the bit rate constant.</summary>
    public const int NullPid = 0x1FFF;

    public const int PatPid = 0x0000;

    public const int CatPid = 0x0001;

    /// <summary>Where EN 300 468 puts the NIT unless the PAT names another pid for it.</summary>
    public const int NitPid = 0x0010;

    /// <summary>SDT and BAT share this pid (EN 300 468, 5.1.3).</summary>
    public const int SdtPid = 0x0011;

    public static int Pid(ReadOnlySpan<byte> packet) => ((packet[1] & 0x1F) << 8) | packet[2];

    public static bool HasTransportError(ReadOnlySpan<byte> packet) => (packet[1] & 0x80) != 0;

    public static bool IsPayloadUnitStart(ReadOnlySpan<byte> packet) => (packet[1] & 0x40) != 0;

    /// <summary>Zero when the payload is in the clear; 2 and 3 are the even and odd key in DVB.</summary>
    public static int ScramblingControl(ReadOnlySpan<byte> packet) => (packet[3] >> 6) & 0x03;

    public static int ContinuityCounter(ReadOnlySpan<byte> packet) => packet[3] & 0x0F;

    public static bool HasPayload(ReadOnlySpan<byte> packet) => (packet[3] & 0x10) != 0;

    /// <summary>
    /// Whether the packet begins something a decoder can start on: the random access indicator
    /// of the adaptation field (ISO/IEC 13818-1, 2.4.3.4), which a broadcaster sets on the packet
    /// that carries the beginning of a picture that stands on its own, with the parameter sets
    /// it needs in front of it.
    /// </summary>
    public static bool IsRandomAccessPoint(ReadOnlySpan<byte> packet) =>
        IsPayloadUnitStart(packet) && (packet[3] & 0x20) != 0 && packet[4] > 0 && (packet[5] & 0x40) != 0;

    /// <summary>
    /// Where the payload starts, after the adaptation field if there is one. Returns
    /// <see cref="Size"/> for a packet without payload or with an adaptation field that claims
    /// the whole packet.
    /// </summary>
    public static int PayloadOffset(ReadOnlySpan<byte> packet)
    {
        if (!HasPayload(packet))
        {
            return Size;
        }

        if ((packet[3] & 0x20) == 0)
        {
            return 4;
        }

        var offset = 5 + packet[4];

        return offset > Size ? Size : offset;
    }

    /// <summary>
    /// Writes the header of a packet this plugin makes itself: payload only, no adaptation field,
    /// never scrambled.
    /// </summary>
    public static void WriteHeader(Span<byte> packet, int pid, bool payloadUnitStart, int continuityCounter)
    {
        packet[0] = SyncByte;
        packet[1] = (byte)((payloadUnitStart ? 0x40 : 0x00) | ((pid >> 8) & 0x1F));
        packet[2] = (byte)(pid & 0xFF);
        packet[3] = (byte)(0x10 | (continuityCounter & 0x0F));
    }
}
