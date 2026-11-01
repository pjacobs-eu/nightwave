namespace Nightwave.Tuners.Dvb.Transport;

/// <summary>
/// Finds the payload of an RTP packet (RFC 3550, 5.1): past the fixed header, the contributing
/// sources and a header extension, short of any padding. SAT>IP sends MPEG-2 TS in it
/// (RFC 2250), seven packets to a datagram as a rule.
/// </summary>
internal static class RtpPacket
{
    public const int PayloadTypeMp2t = 33;

    /// <summary>False for anything that is not an RTP version 2 packet with a payload.</summary>
    public static bool TryGetPayload(ReadOnlySpan<byte> packet, out Range payload, out int sequenceNumber)
    {
        payload = default;
        sequenceNumber = 0;

        if (packet.Length < 12 || (packet[0] >> 6) != 2)
        {
            return false;
        }

        var hasPadding = (packet[0] & 0x20) != 0;
        var hasExtension = (packet[0] & 0x10) != 0;
        var contributors = packet[0] & 0x0F;

        sequenceNumber = (packet[2] << 8) | packet[3];

        var offset = 12 + (4 * contributors);

        if (hasExtension)
        {
            if (offset + 4 > packet.Length)
            {
                return false;
            }

            var words = (packet[offset + 2] << 8) | packet[offset + 3];
            offset += 4 + (4 * words);
        }

        var end = packet.Length;

        if (hasPadding)
        {
            end -= packet[^1];
        }

        if (offset >= end || end > packet.Length)
        {
            return false;
        }

        payload = offset..end;

        return true;
    }
}
