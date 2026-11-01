using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb.Channels;
using Nightwave.Tuners.Dvb.Mpeg.Psi;
using Nightwave.Tuners.Dvb.Tuning;

namespace Nightwave.Tuners.Dvb.Scanning;

/// <summary>Turns what a transponder said into channels to keep.</summary>
internal static class ChannelFactory
{
    /// <summary>
    /// A channel from a scanned service, or null for a service that is neither television nor
    /// radio - data carousels, software updates, NVOD reference services.
    /// </summary>
    /// <remarks>
    /// Only what the transponder actually said is not a channel is dropped. A service that is
    /// simply off the air at the moment of the scan looks like nothing at all - its program map
    /// is empty or is not being transmitted, so there is no sound and no picture to read a kind
    /// off - and it is kept. A channel that broadcasts in the evenings, a regional window, a
    /// season that has not started: none of them should disappear from somebody's list because
    /// of the hour a scan happened to run at, and reappear only if they scan again at the right
    /// moment.
    /// <para>
    /// What is kept that way carries no pids, which costs nothing: the feed reads the program
    /// map again when the channel is tuned and takes its pids from that, because a service can
    /// move its pids between two scans anyway. A service that is still transmitting nothing
    /// then fails at that point with a message saying so, which is the honest answer to "play
    /// this" - and a better one than never having offered the channel.
    /// </para>
    /// </remarks>
    public static StoredChannel? FromService(
        ScanInput input,
        TuningParameters tuning,
        MuxScanResult transponder,
        ScannedService service,
        int? originalNetworkId,
        int? logicalChannel)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(tuning);
        ArgumentNullException.ThrowIfNull(transponder);
        ArgumentNullException.ThrowIfNull(service);

        var description = service.Description;
        var programMap = service.ProgramMap;

        ChannelKind? kind = description switch
        {
            { IsRadio: true } => ChannelKind.Radio,
            { IsTelevision: true } => ChannelKind.Television,

            // The transponder named a type, and it is neither: a data carousel, a software
            // update, an NVOD reference. This is the one place something is actually known
            // about the service, and what is known is that it is not a channel.
            { ServiceType: not null } => null,

            _ when programMap?.Streams.Any(stream => stream.Kind == StreamKind.Video) == true => ChannelKind.Television,
            _ when programMap?.Streams.Any(stream => stream.Kind == StreamKind.Audio) == true => ChannelKind.Radio,

            // A program map that lists streams and not one of them sound or picture. Also
            // known, also not a channel.
            _ when programMap?.Streams.Count > 0 => null,

            // And here nothing is known at all: no description, and a program map that is
            // empty or was never transmitted. That is what a service off the air looks like, so
            // it is kept rather than thrown away. Television because that is what a service
            // carried on a transponder and switched off almost always is, and because filing it
            // as radio would put it somewhere nobody looking for it would go.
            _ => ChannelKind.Television,
        };

        if (kind is null)
        {
            return null;
        }

        var onid = transponder.OriginalNetworkId ?? originalNetworkId ?? 0;

        return new StoredChannel
        {
            Id = StoredChannel.ServiceKey(input.ChannelSource, onid, transponder.TransportStreamId, service.ServiceId),
            Name = description?.Name ?? $"Service {service.ServiceId}",
            Provider = description?.ProviderName,
            Number = logicalChannel?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Kind = kind.Value,
            ServiceType = description?.ServiceType,
            Scrambled = description?.IsScrambled == true || programMap?.HasConditionalAccess == true,
            CaSystems = programMap is null ? [] : [.. programMap.ConditionalAccess.Select(system => system.SystemId).Distinct()],
            Delivery = input.Delivery,
            OrbitalPosition = input.Position?.Tenths,
            DeviceId = input.Delivery == DeliverySystem.Satellite ? null : input.Device.Id,
            Tuning = tuning,
            OriginalNetworkId = onid,
            TransportStreamId = transponder.TransportStreamId,
            ServiceId = service.ServiceId,
            PmtPid = service.PmtPid,
            PcrPid = programMap?.PcrPid ?? 0,
            Pids = programMap is null
                ? []
                : [.. programMap.Streams.Where(stream => stream.Kind != StreamKind.Data).Select(stream => stream.Pid).Distinct()],
            EcmPids = programMap is null ? [] : [.. programMap.EcmPids],
        };
    }
}
