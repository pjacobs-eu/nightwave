namespace Nightwave.Tuners.Dvb.Mpeg.Psi;

/// <summary>
/// The PAT (ISO/IEC 13818-1, 2.4.4.3): which programs a transport stream carries and the pid
/// of each one's program map.
/// </summary>
internal sealed record ProgramAssociationTable
{
    public const int TableId = 0x00;

    public required int TransportStreamId { get; init; }

    public required int Version { get; init; }

    public required int SectionNumber { get; init; }

    public required int LastSectionNumber { get; init; }

    /// <summary>Program number to PMT pid. Program 0 is not in here - see <see cref="NetworkPid"/>.</summary>
    public required IReadOnlyDictionary<int, int> Programs { get; init; }

    /// <summary>Where the NIT is, when the PAT says; <see cref="TransportPacket.NitPid"/> otherwise.</summary>
    public int? NetworkPid { get; init; }

    public static ProgramAssociationTable? Parse(ReadOnlySpan<byte> section)
    {
        if (!SectionHeader.TryRead(section, out var header) || header.TableId != TableId || !header.IsCurrent)
        {
            return null;
        }

        var body = SectionHeader.Body(section);
        var programs = new Dictionary<int, int>();
        int? networkPid = null;

        for (var position = 0; position + 4 <= body.Length; position += 4)
        {
            var number = (body[position] << 8) | body[position + 1];
            var pid = ((body[position + 2] & 0x1F) << 8) | body[position + 3];

            if (number == 0)
            {
                networkPid = pid;
            }
            else
            {
                programs[number] = pid;
            }
        }

        return new ProgramAssociationTable
        {
            TransportStreamId = header.TableIdExtension,
            Version = header.Version,
            SectionNumber = header.SectionNumber,
            LastSectionNumber = header.LastSectionNumber,
            Programs = programs,
            NetworkPid = networkPid,
        };
    }
}
