namespace Nightwave.Tuners.Dvb.Mpeg.Psi;

/// <summary>Writes the PSI this plugin makes itself: the single program PAT of a filtered service.</summary>
internal static class SectionPacketizer
{
    /// <summary>
    /// A PAT that lists one program (ISO/IEC 13818-1, 2.4.4.3), CRC included. What a player sees
    /// in front of a service cut out of a multiplex, so it does not go looking for the others.
    /// </summary>
    public static byte[] ProgramAssociation(int transportStreamId, int version, int programNumber, int programMapPid)
    {
        var section = new byte[8 + 4 + 4];

        section[0] = ProgramAssociationTable.TableId;

        // Section syntax indicator, the zero bit, two reserved bits, and the length after this field.
        var length = section.Length - 3;
        section[1] = (byte)(0xB0 | ((length >> 8) & 0x0F));
        section[2] = (byte)length;
        section[3] = (byte)(transportStreamId >> 8);
        section[4] = (byte)transportStreamId;
        section[5] = (byte)(0xC1 | ((version & 0x1F) << 1));
        section[6] = 0;
        section[7] = 0;
        section[8] = (byte)(programNumber >> 8);
        section[9] = (byte)programNumber;
        section[10] = (byte)(0xE0 | ((programMapPid >> 8) & 0x1F));
        section[11] = (byte)programMapPid;

        MpegCrc32.Seal(section);

        return section;
    }

    /// <summary>
    /// Spreads a section over as many packets as it needs, pointer field first, the rest of the
    /// last packet stuffed with 0xFF. Returns the continuity counter the next packet on the pid
    /// takes.
    /// </summary>
    public static int Packetize(ReadOnlySpan<byte> section, int pid, int continuityCounter, List<byte[]> packets)
    {
        ArgumentNullException.ThrowIfNull(packets);

        var remaining = section;
        var first = true;

        while (first || !remaining.IsEmpty)
        {
            var packet = new byte[TransportPacket.Size];
            TransportPacket.WriteHeader(packet, pid, first, continuityCounter);
            continuityCounter = (continuityCounter + 1) & 0x0F;

            var offset = 4;
            if (first)
            {
                packet[offset++] = 0;
            }

            var take = Math.Min(remaining.Length, TransportPacket.Size - offset);
            remaining[..take].CopyTo(packet.AsSpan(offset));
            packet.AsSpan(offset + take).Fill(0xFF);
            remaining = remaining[take..];

            packets.Add(packet);
            first = false;
        }

        return continuityCounter;
    }

    /// <summary>How many packets <see cref="Packetize"/> makes of a section this long.</summary>
    public static int PacketCount(int sectionLength)
    {
        var firstCapacity = TransportPacket.Size - 5;

        return sectionLength <= firstCapacity
            ? 1
            : 1 + (int)Math.Ceiling((sectionLength - firstCapacity) / (double)(TransportPacket.Size - 4));
    }
}
