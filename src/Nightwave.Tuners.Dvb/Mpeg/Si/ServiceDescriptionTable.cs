using Nightwave.Tuners.Dvb.Mpeg.Psi;

namespace Nightwave.Tuners.Dvb.Mpeg.Si;

/// <summary>
/// An SDT section (EN 300 468, 5.2.3): the names of the services on a transport stream, their
/// type, and whether they are scrambled.
/// </summary>
internal sealed record ServiceDescriptionTable
{
    public const int ActualTableId = 0x42;
    public const int OtherTableId = 0x46;

    public required int TableId { get; init; }

    public required int TransportStreamId { get; init; }

    public required int OriginalNetworkId { get; init; }

    public required int Version { get; init; }

    public required int SectionNumber { get; init; }

    public required int LastSectionNumber { get; init; }

    public required IReadOnlyList<ServiceDescription> Services { get; init; }

    public bool IsActual => TableId == ActualTableId;

    public static ServiceDescriptionTable? Parse(ReadOnlySpan<byte> section)
    {
        if (!SectionHeader.TryRead(section, out var header)
            || header.TableId is not (ActualTableId or OtherTableId)
            || !header.IsCurrent)
        {
            return null;
        }

        var body = SectionHeader.Body(section);
        if (body.Length < 3)
        {
            return null;
        }

        var services = new List<ServiceDescription>();
        var position = 3;

        while (position + 5 <= body.Length)
        {
            var serviceId = (body[position] << 8) | body[position + 1];
            var running = (body[position + 3] >> 5) & 0x07;
            var freeCaMode = (body[position + 3] & 0x10) != 0;
            var loopLength = ((body[position + 3] & 0x0F) << 8) | body[position + 4];

            if (position + 5 + loopLength > body.Length)
            {
                break;
            }

            var descriptors = Descriptor.ReadLoop(body.Slice(position + 5, loopLength));
            services.Add(ServiceDescription.Create(serviceId, running, freeCaMode, descriptors));
            position += 5 + loopLength;
        }

        return new ServiceDescriptionTable
        {
            TableId = header.TableId,
            TransportStreamId = header.TableIdExtension,
            OriginalNetworkId = (body[0] << 8) | body[1],
            Version = header.Version,
            SectionNumber = header.SectionNumber,
            LastSectionNumber = header.LastSectionNumber,
            Services = services,
        };
    }
}

/// <summary>One service as the SDT describes it.</summary>
internal sealed record ServiceDescription(
    int ServiceId,
    int RunningStatus,
    bool IsScrambled,
    int? ServiceType,
    string? ProviderName,
    string? Name)
{
    public static ServiceDescription Create(int serviceId, int running, bool scrambled, IReadOnlyList<Descriptor> descriptors)
    {
        var descriptor = descriptors.FirstOrDefault(candidate => candidate.Tag == Descriptor.Service);

        if (descriptor is null || descriptor.Data.Length < 3)
        {
            return new ServiceDescription(serviceId, running, scrambled, null, null, null);
        }

        var data = descriptor.Data.AsSpan();
        var type = data[0];
        var providerLength = data[1];

        if (2 + providerLength + 1 > data.Length)
        {
            return new ServiceDescription(serviceId, running, scrambled, type, null, null);
        }

        var provider = DvbText.Decode(data.Slice(2, providerLength));
        var nameLength = data[2 + providerLength];
        var nameStart = 3 + providerLength;
        var name = nameStart + nameLength <= data.Length ? DvbText.Decode(data.Slice(nameStart, nameLength)) : null;

        return new ServiceDescription(
            serviceId,
            running,
            scrambled,
            type,
            string.IsNullOrEmpty(provider) ? null : provider,
            string.IsNullOrEmpty(name) ? null : name);
    }

    /// <summary>
    /// Service types that are television (EN 300 468, table 87): SD, HD and UHD in MPEG-2, H.264
    /// and HEVC, including the NVOD and frame compatible 3D variants.
    /// </summary>
    public bool IsTelevision => ServiceType is 0x01 or 0x11 or 0x16 or 0x17 or 0x18 or 0x19 or 0x1A or 0x1B
        or 0x1C or 0x1D or 0x1E or 0x1F or 0x20;

    /// <summary>Service types that are radio: MPEG-1 Layer II, FM, and advanced codec radio.</summary>
    public bool IsRadio => ServiceType is 0x02 or 0x07 or 0x0A;
}
