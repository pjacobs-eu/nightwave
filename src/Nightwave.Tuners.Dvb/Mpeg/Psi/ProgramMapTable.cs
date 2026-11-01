using System.Text;

namespace Nightwave.Tuners.Dvb.Mpeg.Psi;

/// <summary>
/// A PMT (ISO/IEC 13818-1, 2.4.4.8): the elementary streams of one program, its PCR pid, and
/// the CA descriptors that say it is scrambled and where its ECMs are.
/// </summary>
internal sealed record ProgramMapTable
{
    public const int TableId = 0x02;

    public required int ProgramNumber { get; init; }

    public required int Version { get; init; }

    /// <summary>0x1FFF when the program has no PCR, which a radio service sometimes does.</summary>
    public required int PcrPid { get; init; }

    public required IReadOnlyList<Descriptor> Descriptors { get; init; }

    public required IReadOnlyList<ElementaryStream> Streams { get; init; }

    /// <summary>The section exactly as it was received, table header to CRC.</summary>
    public required byte[] Section { get; init; }

    /// <summary>CA descriptors at program level and on every stream.</summary>
    public IEnumerable<ConditionalAccessDescriptor> ConditionalAccess =>
        Descriptors
            .Concat(Streams.SelectMany(stream => stream.Descriptors))
            .Select(ConditionalAccessDescriptor.From)
            .OfType<ConditionalAccessDescriptor>();

    /// <summary>Whether anything in the program map says a conditional access system is involved.</summary>
    public bool HasConditionalAccess => ConditionalAccess.Any();

    /// <summary>The pids the CA descriptors carry their ECMs on.</summary>
    public IReadOnlySet<int> EcmPids => ConditionalAccess.Select(system => system.Pid).ToHashSet();

    /// <summary>Every pid a player needs for this program: its streams and its clock.</summary>
    public IReadOnlySet<int> PlayablePids
    {
        get
        {
            var pids = Streams.Select(stream => stream.Pid).ToHashSet();

            if (PcrPid != TransportPacket.NullPid)
            {
                pids.Add(PcrPid);
            }

            return pids;
        }
    }

    public static ProgramMapTable? Parse(ReadOnlySpan<byte> section)
    {
        if (!SectionHeader.TryRead(section, out var header) || header.TableId != TableId || !header.IsCurrent)
        {
            return null;
        }

        var body = SectionHeader.Body(section);
        if (body.Length < 4)
        {
            return null;
        }

        var pcrPid = ((body[0] & 0x1F) << 8) | body[1];
        var programInfoLength = ((body[2] & 0x0F) << 8) | body[3];

        if (4 + programInfoLength > body.Length)
        {
            return null;
        }

        var descriptors = Descriptor.ReadLoop(body.Slice(4, programInfoLength));
        var streams = new List<ElementaryStream>();
        var position = 4 + programInfoLength;

        while (position + 5 <= body.Length)
        {
            var streamType = body[position];
            var pid = ((body[position + 1] & 0x1F) << 8) | body[position + 2];
            var infoLength = ((body[position + 3] & 0x0F) << 8) | body[position + 4];

            if (position + 5 + infoLength > body.Length)
            {
                break;
            }

            streams.Add(ElementaryStream.Create(streamType, pid, Descriptor.ReadLoop(body.Slice(position + 5, infoLength))));
            position += 5 + infoLength;
        }

        return new ProgramMapTable
        {
            ProgramNumber = header.TableIdExtension,
            Version = header.Version,
            PcrPid = pcrPid,
            Descriptors = descriptors,
            Streams = streams,
            Section = section.ToArray(),
        };
    }
}

/// <summary>What an elementary stream carries, as far as a tuner needs to know.</summary>
internal enum StreamKind
{
    Video,
    Audio,
    Subtitles,
    Teletext,
    Data,
}

/// <summary>One stream of a program, classified from its stream type and descriptors.</summary>
internal sealed record ElementaryStream(
    int StreamType,
    int Pid,
    StreamKind Kind,
    string? Codec,
    string? Language,
    IReadOnlyList<Descriptor> Descriptors)
{
    /// <summary>
    /// Classifies a stream. Stream type 0x06 (PES private data) is what DVB uses for AC-3,
    /// E-AC-3, subtitles and teletext alike, so for that one the descriptors decide
    /// (EN 300 468, Annex D and 6.2).
    /// </summary>
    public static ElementaryStream Create(int streamType, int pid, IReadOnlyList<Descriptor> descriptors)
    {
        var (kind, codec) = streamType switch
        {
            0x01 => (StreamKind.Video, "mpeg1video"),
            0x02 => (StreamKind.Video, "mpeg2video"),
            0x10 => (StreamKind.Video, "mpeg4"),
            0x1B => (StreamKind.Video, "h264"),
            0x24 => (StreamKind.Video, "hevc"),
            0x33 => (StreamKind.Video, "vvc"),
            0x03 => (StreamKind.Audio, "mp2"),
            0x04 => (StreamKind.Audio, "mp2"),
            0x0F => (StreamKind.Audio, "aac"),
            0x11 => (StreamKind.Audio, "aac_latm"),
            0x81 => (StreamKind.Audio, "ac3"),
            0x87 => (StreamKind.Audio, "eac3"),
            0x06 => FromDescriptors(descriptors),
            _ => (StreamKind.Data, null),
        };

        return new ElementaryStream(streamType, pid, kind, codec, LanguageOf(descriptors), descriptors);
    }

    private static (StreamKind Kind, string? Codec) FromDescriptors(IReadOnlyList<Descriptor> descriptors)
    {
        foreach (var descriptor in descriptors)
        {
            switch (descriptor.Tag)
            {
                case Descriptor.Ac3:
                    return (StreamKind.Audio, "ac3");
                case Descriptor.EnhancedAc3:
                    return (StreamKind.Audio, "eac3");
                case Descriptor.Aac:
                    return (StreamKind.Audio, "aac");
                case Descriptor.Dts:
                    return (StreamKind.Audio, "dts");
                case Descriptor.Subtitling:
                    return (StreamKind.Subtitles, "dvb_subtitle");
                case Descriptor.Teletext:
                    return (StreamKind.Teletext, "dvb_teletext");
                case Descriptor.Extension when descriptor.Data.Length > 0 && descriptor.Data[0] == Descriptor.Ac4Extension:
                    return (StreamKind.Audio, "ac4");
            }
        }

        return (StreamKind.Data, null);
    }

    /// <summary>The language from an ISO 639 descriptor, or from the first entry of a subtitling or teletext one.</summary>
    private static string? LanguageOf(IReadOnlyList<Descriptor> descriptors)
    {
        foreach (var descriptor in descriptors)
        {
            if (descriptor.Tag is Descriptor.Iso639Language or Descriptor.Subtitling or Descriptor.Teletext
                && descriptor.Data.Length >= 3)
            {
                var code = Encoding.ASCII.GetString(descriptor.Data, 0, 3).Trim('\0', ' ');

                if (code.Length == 3 && code.All(char.IsAsciiLetter))
                {
                    return code.ToLowerInvariant();
                }
            }
        }

        return null;
    }
}
