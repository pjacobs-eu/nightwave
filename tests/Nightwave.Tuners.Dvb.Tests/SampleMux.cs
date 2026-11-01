using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb;
using Nightwave.Tuners.Dvb.Channels;
using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.Tuning;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>
/// One transponder the way a satellite carries it: two services in the clear, a scrambled one
/// with its ECMs, the CAT with an EMM pid, and SDT and NIT.
/// </summary>
internal static class SampleMux
{
    public const int TransportStreamId = 1019;
    public const int NetworkId = 1;

    public const int ClearService = 100;
    public const int ClearPmtPid = 0x100;
    public const int ClearVideoPid = 0x101;
    public const int ClearAudioPid = 0x102;

    public const int OtherService = 200;
    public const int OtherPmtPid = 0x300;
    public const int OtherVideoPid = 0x301;

    public const int ScrambledService = 300;
    public const int ScrambledPmtPid = 0x400;
    public const int ScrambledVideoPid = 0x401;
    public const int EcmPid = 0x200;
    public const int EmmPid = 0x500;

    public static readonly TuningParameters Tuning = new()
    {
        System = DvbSystem.DvbS,
        FrequencyKhz = 11836000,
        Polarization = Polarization.Horizontal,
        SymbolRate = 27500,
        Modulation = "qpsk",
        Fec = "34",
    };

    public static readonly TuningParameters OtherTuning = Tuning with { FrequencyKhz = 12188000 };

    public static byte[] ClearPmt(int version = 0) => TsBuilder.Pmt(
        ClearService,
        version,
        ClearVideoPid,
        [],
        (0x1B, ClearVideoPid, []),
        (0x04, ClearAudioPid, TsBuilder.Descriptor(0x0A, (byte)'d', (byte)'e', (byte)'u', 0)));

    public static byte[] ScrambledPmt(int version = 0) => TsBuilder.Pmt(
        ScrambledService,
        version,
        ScrambledVideoPid,
        TsBuilder.CaDescriptor(0x1830, EcmPid),
        (0x1B, ScrambledVideoPid, []));

    /// <summary>A few rounds of the whole transponder, with a NIT that puts the network at <paramref name="orbital"/>.</summary>
    public static List<byte[]> Packets(int transportStreamId = TransportStreamId, IEnumerable<(int Tsid, TuningParameters Tuning)>? network = null, int orbital = 192)
    {
        var packets = new List<byte[]>();
        var continuity = new Dictionary<int, int>();

        void Add(IEnumerable<byte[]> more)
        {
            foreach (var packet in more)
            {
                var pid = ((packet[1] & 0x1F) << 8) | packet[2];
                var next = continuity.GetValueOrDefault(pid);
                var copy = (byte[])packet.Clone();
                copy[3] = (byte)((copy[3] & 0xF0) | next);
                continuity[pid] = (next + 1) & 0x0F;
                packets.Add(copy);
            }
        }

        var pat = TsBuilder.Pat(transportStreamId, 0, (0, 0x10), (ClearService, ClearPmtPid), (OtherService, OtherPmtPid), (ScrambledService, ScrambledPmtPid));
        var otherPmt = TsBuilder.Pmt(OtherService, 0, OtherVideoPid, [], (0x02, OtherVideoPid, []));
        var cat = TsBuilder.Section(0x01, 0xFFFF, 0, TsBuilder.CaDescriptor(0x1830, EmmPid));
        var sdt = TsBuilder.Sdt(
            transportStreamId,
            NetworkId,
            0,
            (ClearService, false, TsBuilder.ServiceDescriptor(0x19, "ARD", "Das Erste HD")),
            (OtherService, false, TsBuilder.ServiceDescriptor(0x02, "ARD", "Bayern 3")),
            (ScrambledService, true, TsBuilder.ServiceDescriptor(0x19, "Sky", "Sky Cinema")));

        var streams = (network ?? [(transportStreamId, Tuning)])
            .Select(entry => (entry.Tsid, NetworkId, TsBuilder.SatelliteDelivery(entry.Tuning.FrequencyKhz / 1000m, orbital, entry.Tuning.Polarization == Polarization.Horizontal, entry.Tuning.System == DvbSystem.DvbS2, entry.Tuning.SymbolRate ?? 27500, 3)))
            .ToArray();
        var nit = TsBuilder.Nit(NetworkId, 0, TsBuilder.Descriptor(0x40, [.. "ASTRA"u8]), streams);

        for (var round = 0; round < 4; round++)
        {
            Add(TsBuilder.Packets(pat, 0));
            Add(TsBuilder.Packets(cat, 1));
            Add(TsBuilder.Packets(ClearPmt(), ClearPmtPid));
            Add(TsBuilder.Packets(otherPmt, OtherPmtPid));
            Add(TsBuilder.Packets(ScrambledPmt(), ScrambledPmtPid));
            Add(TsBuilder.Packets(sdt, 0x11));
            Add(TsBuilder.Packets(nit, 0x10));

            for (var index = 0; index < 4; index++)
            {
                // The first picture of every round stands on its own, as a broadcast's does:
                // that is where a player joining the channel is handed the stream.
                var keyframe = index == 0;

                Add([keyframe ? TsBuilder.RandomAccessPacket(ClearVideoPid, 0, 0xA1) : TsBuilder.EsPacket(ClearVideoPid, 0, 0xA1)]);
                Add([TsBuilder.EsPacket(ClearAudioPid, 0, 0xA2)]);
                Add([TsBuilder.EsPacket(OtherVideoPid, 0, 0xB1)]);
                Add([keyframe
                    ? TsBuilder.RandomAccessPacket(ScrambledVideoPid, 0, 0xC1, scrambling: 2)
                    : TsBuilder.EsPacket(ScrambledVideoPid, 0, 0xC1, scrambling: 2)]);
                Add([TsBuilder.EsPacket(EcmPid, 0, 0xEC)]);
                Add([TsBuilder.EsPacket(EmmPid, 0, 0xEE)]);
                Add([TsBuilder.EsPacket(0x1FFF, 0, 0xFF)]);
            }
        }

        return packets;
    }

    public static TunerDevice Device(int tuners = 2, string host = "192.0.2.10") => new()
    {
        Id = $"satip:{host}",
        Name = "Test SAT>IP",
        Host = host,
        Tuners = new Dictionary<DeliverySystem, int> { [DeliverySystem.Satellite] = tuners },
        TunersKnown = true,
        SatelliteInputs = [new SatelliteInput(1, Position)],
    };

    public static OrbitalPosition Position => new(192);

    public static StoredChannel Channel(int service, int pmtPid, TuningParameters? tuning = null, bool scrambled = false, string? name = null) => new()
    {
        Id = StoredChannel.ServiceKey("19.2E", NetworkId, TransportStreamId, service),
        Name = name ?? $"Service {service}",
        Delivery = DeliverySystem.Satellite,
        OrbitalPosition = 192,
        Tuning = tuning ?? Tuning,
        OriginalNetworkId = NetworkId,
        TransportStreamId = TransportStreamId,
        ServiceId = service,
        PmtPid = pmtPid,
        Scrambled = scrambled,
    };

    public static DvbSettings Settings() => new();
}
