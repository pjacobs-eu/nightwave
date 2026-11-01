using Nightwave.Tuners.Dvb.Mpeg.Psi;
using Nightwave.Tuners.Dvb.Tuning;

namespace Nightwave.Tuners.Dvb.Mpeg.Si;

/// <summary>
/// A NIT section (EN 300 468, 5.2.1): the transport streams of a network, and for each one how
/// to tune it. What a scan reads to learn every transponder from one.
/// </summary>
internal sealed record NetworkInformationTable
{
    public const int ActualTableId = 0x40;

    public required int NetworkId { get; init; }

    public required int Version { get; init; }

    public required int SectionNumber { get; init; }

    public required int LastSectionNumber { get; init; }

    public string? NetworkName { get; init; }

    public required IReadOnlyList<NetworkTransportStream> TransportStreams { get; init; }

    public static NetworkInformationTable? Parse(ReadOnlySpan<byte> section)
    {
        if (!SectionHeader.TryRead(section, out var header) || header.TableId != ActualTableId || !header.IsCurrent)
        {
            return null;
        }

        var body = SectionHeader.Body(section);
        if (body.Length < 2)
        {
            return null;
        }

        var networkLength = ((body[0] & 0x0F) << 8) | body[1];
        if (2 + networkLength + 2 > body.Length)
        {
            return null;
        }

        var networkDescriptors = Descriptor.ReadLoop(body.Slice(2, networkLength));
        var name = networkDescriptors.FirstOrDefault(descriptor => descriptor.Tag == Descriptor.NetworkName);

        var position = 2 + networkLength;
        var loopLength = ((body[position] & 0x0F) << 8) | body[position + 1];
        position += 2;

        var end = Math.Min(body.Length, position + loopLength);
        var streams = new List<NetworkTransportStream>();

        while (position + 6 <= end)
        {
            var transportStreamId = (body[position] << 8) | body[position + 1];
            var originalNetworkId = (body[position + 2] << 8) | body[position + 3];
            var descriptorsLength = ((body[position + 4] & 0x0F) << 8) | body[position + 5];

            if (position + 6 + descriptorsLength > end)
            {
                break;
            }

            var descriptors = Descriptor.ReadLoop(body.Slice(position + 6, descriptorsLength));
            streams.Add(NetworkTransportStream.Create(transportStreamId, originalNetworkId, descriptors));
            position += 6 + descriptorsLength;
        }

        return new NetworkInformationTable
        {
            NetworkId = header.TableIdExtension,
            Version = header.Version,
            SectionNumber = header.SectionNumber,
            LastSectionNumber = header.LastSectionNumber,
            NetworkName = name is null ? null : DvbText.Decode(name.Data),
            TransportStreams = streams,
        };
    }
}

/// <summary>One transport stream of a network: how to tune it, and what numbers its services have.</summary>
internal sealed record NetworkTransportStream
{
    public required int TransportStreamId { get; init; }

    public required int OriginalNetworkId { get; init; }

    /// <summary>Null when the NIT carries no delivery descriptor this plugin reads.</summary>
    public TuningParameters? Tuning { get; init; }

    /// <summary>Orbital position in tenths of a degree, east positive, for a satellite transport stream.</summary>
    public int? OrbitalPosition { get; init; }

    /// <summary>Service id to logical channel number, where the network numbers its channels.</summary>
    public IReadOnlyDictionary<int, int> LogicalChannels { get; init; } = new Dictionary<int, int>();

    public static NetworkTransportStream Create(int transportStreamId, int originalNetworkId, IReadOnlyList<Descriptor> descriptors)
    {
        TuningParameters? tuning = null;
        int? orbital = null;
        var channels = new Dictionary<int, int>();
        int? plp = null;
        int? t2SystemId = null;

        foreach (var descriptor in descriptors)
        {
            switch (descriptor.Tag)
            {
                case Descriptor.SatelliteDeliverySystem:
                    (tuning, orbital) = DeliveryDescriptors.Satellite(descriptor.Data) is { } satellite
                        ? (satellite.Tuning, satellite.OrbitalPosition)
                        : (tuning, orbital);
                    break;
                case Descriptor.CableDeliverySystem:
                    tuning = DeliveryDescriptors.Cable(descriptor.Data) ?? tuning;
                    break;
                case Descriptor.TerrestrialDeliverySystem:
                    tuning = DeliveryDescriptors.Terrestrial(descriptor.Data) ?? tuning;
                    break;
                case Descriptor.Extension when descriptor.Data.Length >= 4
                    && descriptor.Data[0] == Descriptor.T2DeliverySystemExtension:
                    plp = descriptor.Data[1];
                    t2SystemId = (descriptor.Data[2] << 8) | descriptor.Data[3];
                    break;
                case Descriptor.LogicalChannel:
                    // EACEM / NorDig v1 layout: service id, visible flag, ten bit channel number.
                    for (var position = 0; position + 4 <= descriptor.Data.Length; position += 4)
                    {
                        var serviceId = (descriptor.Data[position] << 8) | descriptor.Data[position + 1];
                        var number = ((descriptor.Data[position + 2] & 0x03) << 8) | descriptor.Data[position + 3];

                        if (number > 0)
                        {
                            channels[serviceId] = number;
                        }
                    }

                    break;
            }
        }

        // A T2 descriptor next to a terrestrial one makes the multiplex a DVB-T2 one.
        if (tuning is { System: DvbSystem.DvbT } && plp is not null)
        {
            tuning = tuning with { System = DvbSystem.DvbT2, Plp = plp, T2SystemId = t2SystemId };
        }

        return new NetworkTransportStream
        {
            TransportStreamId = transportStreamId,
            OriginalNetworkId = originalNetworkId,
            Tuning = tuning,
            OrbitalPosition = orbital,
            LogicalChannels = channels,
        };
    }
}

/// <summary>The three delivery system descriptors (EN 300 468, 6.2.13), turned into tuning parameters.</summary>
internal static class DeliveryDescriptors
{
    private static readonly string?[] SatelliteFec = [null, "12", "23", "34", "56", "78", "89", "35", "45", "910"];

    private static readonly string?[] CableModulation = [null, "16qam", "32qam", "64qam", "128qam", "256qam"];

    /// <summary>The satellite delivery system descriptor: frequency, position, polarization, modulation, symbol rate.</summary>
    public static (TuningParameters Tuning, int OrbitalPosition)? Satellite(ReadOnlySpan<byte> data)
    {
        if (data.Length < 11)
        {
            return null;
        }

        // Frequency in 10 kHz steps, eight BCD digits: 01183600 is 11.83600 GHz.
        var frequency = Bcd(data[..4], 8) * 10;
        var orbital = (int)Bcd(data.Slice(4, 2), 4);
        var east = (data[6] & 0x80) != 0;
        var polarization = ((data[6] >> 5) & 0x03) switch
        {
            0 => Polarization.Horizontal,
            1 => Polarization.Vertical,
            2 => Polarization.CircularLeft,
            _ => Polarization.CircularRight,
        };
        var s2 = (data[6] & 0x04) != 0;
        var rollOff = ((data[6] >> 3) & 0x03) switch { 1 => "0.25", 2 => "0.20", _ => "0.35" };
        var modulation = (data[6] & 0x03) switch { 2 => "8psk", 3 => "16apsk", _ => "qpsk" };

        // Symbol rate in 100 symbol steps, seven BCD digits: 0275000 is 27.5000 Msymbol/s.
        var symbolRate = (int)(SymbolRateDigits(data) / 10);
        var fec = data[10] & 0x0F;

        var tuning = new TuningParameters
        {
            System = s2 ? DvbSystem.DvbS2 : DvbSystem.DvbS,
            FrequencyKhz = (int)frequency,
            Polarization = polarization,
            SymbolRate = symbolRate,
            Modulation = s2 ? modulation : "qpsk",
            RollOff = s2 ? rollOff : "0.35",
            Fec = fec < SatelliteFec.Length ? SatelliteFec[fec] : null,
        };

        return (tuning, east ? orbital : -orbital);
    }

    /// <summary>The cable delivery system descriptor: frequency, modulation, symbol rate.</summary>
    public static TuningParameters? Cable(ReadOnlySpan<byte> data)
    {
        if (data.Length < 11)
        {
            return null;
        }

        // Frequency in 100 Hz steps, eight BCD digits: 03460000 is 346.0000 MHz.
        var frequency = Bcd(data[..4], 8) / 10;
        var modulation = data[6];
        var symbolRate = (int)(SymbolRateDigits(data) / 10);
        var fec = data[10] & 0x0F;

        return new TuningParameters
        {
            System = DvbSystem.DvbC,
            FrequencyKhz = (int)frequency,
            SymbolRate = symbolRate,
            Modulation = modulation < CableModulation.Length ? CableModulation[modulation] : null,
            Fec = fec < SatelliteFec.Length ? SatelliteFec[fec] : null,
        };
    }

    /// <summary>The terrestrial delivery system descriptor: centre frequency, bandwidth, constellation, guard, mode.</summary>
    public static TuningParameters? Terrestrial(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7)
        {
            return null;
        }

        // Centre frequency in 10 Hz steps, binary this time.
        var frequency = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]) / 100;

        return new TuningParameters
        {
            System = DvbSystem.DvbT,
            FrequencyKhz = (int)frequency,
            Bandwidth = ((data[4] >> 5) & 0x07) switch { 0 => "8", 1 => "7", 2 => "6", 3 => "5", _ => null },
            Modulation = ((data[5] >> 6) & 0x03) switch { 0 => "qpsk", 1 => "16qam", 2 => "64qam", _ => null },
            Fec = (data[5] & 0x07) switch { 0 => "12", 1 => "23", 2 => "34", 3 => "56", 4 => "78", _ => null },
            GuardInterval = ((data[6] >> 3) & 0x03) switch { 0 => "132", 1 => "116", 2 => "18", _ => "14" },
            TransmissionMode = ((data[6] >> 1) & 0x03) switch { 0 => "2k", 1 => "8k", 2 => "4k", _ => null },
        };
    }

    /// <summary>Seven BCD digits: bytes 7 to 9 and the upper half of byte 10.</summary>
    private static long SymbolRateDigits(ReadOnlySpan<byte> data) =>
        (Bcd(data.Slice(7, 3), 6) * 10) + ((data[10] >> 4) & 0x0F);

    /// <summary>Reads <paramref name="digits"/> binary coded decimal digits, two to a byte.</summary>
    public static long Bcd(ReadOnlySpan<byte> data, int digits)
    {
        long value = 0;

        for (var index = 0; index < digits; index++)
        {
            var nibble = (index % 2 == 0 ? data[index / 2] >> 4 : data[index / 2]) & 0x0F;
            value = (value * 10) + Math.Min(nibble, 9);
        }

        return value;
    }
}
