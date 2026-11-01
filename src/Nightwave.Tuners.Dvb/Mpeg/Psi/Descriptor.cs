namespace Nightwave.Tuners.Dvb.Mpeg.Psi;

/// <summary>One tag-length-value descriptor, as the PSI and SI loops carry them.</summary>
internal sealed record Descriptor(int Tag, byte[] Data)
{
    public const int ConditionalAccess = 0x09;
    public const int Iso639Language = 0x0A;
    public const int NetworkName = 0x40;
    public const int ServiceList = 0x41;
    public const int SatelliteDeliverySystem = 0x43;
    public const int CableDeliverySystem = 0x44;
    public const int Service = 0x48;
    public const int Teletext = 0x56;
    public const int Subtitling = 0x59;
    public const int TerrestrialDeliverySystem = 0x5A;
    public const int Ac3 = 0x6A;
    public const int EnhancedAc3 = 0x7A;
    public const int Dts = 0x7B;
    public const int Aac = 0x7C;
    public const int Extension = 0x7F;
    public const int LogicalChannel = 0x83;

    /// <summary>Tag extension of the T2 delivery system descriptor (EN 300 468, 6.4.6.3).</summary>
    public const int T2DeliverySystemExtension = 0x04;

    /// <summary>Tag extension of the AC-4 descriptor.</summary>
    public const int Ac4Extension = 0x15;

    /// <summary>
    /// Reads a descriptor loop. A descriptor whose length runs past the loop ends it: what came
    /// before is still good, what would follow cannot be found.
    /// </summary>
    public static IReadOnlyList<Descriptor> ReadLoop(ReadOnlySpan<byte> loop)
    {
        var descriptors = new List<Descriptor>();
        var position = 0;

        while (position + 2 <= loop.Length)
        {
            var tag = loop[position];
            var length = loop[position + 1];

            if (position + 2 + length > loop.Length)
            {
                break;
            }

            descriptors.Add(new Descriptor(tag, loop.Slice(position + 2, length).ToArray()));
            position += 2 + length;
        }

        return descriptors;
    }
}

/// <summary>
/// A CA descriptor (ISO/IEC 13818-1, 2.6.16): which conditional access system, and the pid its
/// ECMs (in a PMT) or EMMs (in the CAT) are on.
/// </summary>
internal sealed record ConditionalAccessDescriptor(int SystemId, int Pid)
{
    public static ConditionalAccessDescriptor? From(Descriptor descriptor)
    {
        if (descriptor.Tag != Descriptor.ConditionalAccess || descriptor.Data.Length < 4)
        {
            return null;
        }

        var data = descriptor.Data;

        return new ConditionalAccessDescriptor((data[0] << 8) | data[1], ((data[2] & 0x1F) << 8) | data[3]);
    }
}
