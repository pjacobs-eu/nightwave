using System.Buffers.Binary;

namespace Nightwave.Tuners.Dvb.Descrambling;

/// <summary>
/// What conditional access needs from a program map section: its CA descriptors and streams,
/// and the CA PMT built from them.
/// </summary>
/// <remarks>
/// <para>The ca_pmt body (EN 50221 8.4.3.4) as the descrambling server reads it (<c>dvbapi_parse_capmt</c>):</para>
/// <code>
/// u8  ca_pmt_list_management          ADD for a new service, UPDATE for a changed program map
/// u16 program_number
/// u8  reserved(2) version(5) current_next(1)
/// u16 reserved(4) program_info_length  counts everything up to the stream loop
/// u8  ca_pmt_cmd_id                   0x01 ok_descrambling
///     0x81 enigma namespace, tsid, onid (8 bytes; namespace 0: not an Enigma2 receiver)
///     0x83 adapter device   = device number of the slot
///     0x84 pmt pid
///     0x85 service type mask = live TV (see below)
///     0x86 demux device     = device number of the slot
///     0x87 ca device        = device number of the slot
///     the program's CA descriptors (0x09), as broadcast
/// per stream:
/// u8  stream_type, u16 reserved(3) pid, u16 reserved(4) es_info_length
/// u8  ca_pmt_cmd_id and the stream's CA descriptors, when it has any (es_info_length 0 otherwise)
/// </code>
/// <para>
/// Why one number is adapter, demux and ca device at once: the server echoes the adapter index in
/// every message about a service, so it is what routes them back; CA_STOP names the demux index;
/// and the ca device keeps the server's descrambler index bookkeeping (kept per ca device and
/// stream pid) apart for two services that happen to use the same pid numbers on different
/// transponders. The server allows 32 ca devices, which is why there are at most 32 slots.
/// </para>
/// <para>
/// Why that number counts down from 23 (<see cref="DeviceNumber"/>) rather than up from 0: the
/// server keeps that bookkeeping for all its clients together, not per connection. Other network
/// dvbapi clients number their services from 0 - one per tuner, or per descrambling slot - and
/// when one of them descrambles the same channel on the same ca device number, the stream pids
/// are already taken there with the other client's index. <c>update_streampid_list</c>
/// (module-dvbapi.c) then adds this client's index without sending CA_SET_PID ("Added existing
/// streampid ... with new index"), and <c>dvbapi_write_cw</c> skips every control word for it
/// ("caN is using index M for streampid ... -&gt; skip!"). The server still answers the ECM - from
/// its cache, since the other client asked first - and still sends ECM_INFO, but no key ever
/// arrives. CA_STOP is no safer: the server stops the first demuxer with the named demux index,
/// whichever connection it belongs to. From the top, the numbers stay clear of the others unless
/// the server serves 32 services at once.
/// </para>
/// <para>
/// Why the service type mask, which the server calls optional: without it, a descrambling server
/// built with its stream relay withholds control words for the CAIDs the relay handles - Sky
/// Deutschland's among them - whenever the answer came from its cache. <c>stream_write_cw</c>
/// (module-streamrelay.c) reports every answer that is not <c>E_FOUND</c> as consumed by the
/// relay, and <c>dvbapi_send_dcw</c> (module-dvbapi.c) then writes nothing to the network client.
/// So a channel somebody else had just asked for - another receiver, another client of the same
/// server - stayed scrambled here while the server logged it as answered. A mask without the
/// relay's bits (0x180, types 7 and 8) makes the server send the control word over dvbapi every
/// time. The types are Enigma2's (lib/dvb/pmt.h); type 0 is live television, which is what this is.
/// </para>
/// </remarks>
internal sealed class CaProgramMap
{
    /// <summary>The number of services one server descrambles at once (MAX_DEMUX, and CA_MAX ca devices).</summary>
    public const int MaxSlots = 24;

    /// <summary>Enigma2's service type mask for live television: bit 0 set, the stream relay's bits (0x180) clear.</summary>
    public const uint LiveServiceTypeMask = 1u << 0;

    /// <summary>
    /// The adapter, demux and ca device number the server knows a slot by: slot 0 is 23, slot 1
    /// is 22, and so on, away from the numbers other dvbapi clients use (see the remarks). Emiro
    /// counts down from 31 with the same code, and OSCam keeps two clients with one number on one
    /// demuxer, where each one's channel change ends the other's descrambling.
    /// </summary>
    public static int DeviceNumber(int slot) => MaxSlots - 1 - slot;

    /// <summary>The slot an adapter index in a message from the server stands for, or -1 for none of ours.</summary>
    public static int SlotOfDevice(int device) => device is >= 0 and < MaxSlots ? MaxSlots - 1 - device : -1;

    private CaProgramMap(
        int programNumber,
        int version,
        bool currentNext,
        byte[] programCaDescriptors,
        IReadOnlyList<CaStream> streams,
        IReadOnlyDictionary<int, IReadOnlyList<int>> ecmPids)
    {
        ProgramNumber = programNumber;
        Version = version;
        CurrentNext = currentNext;
        ProgramCaDescriptors = programCaDescriptors;
        Streams = streams;
        EcmPids = ecmPids;
    }

    public int ProgramNumber { get; }

    public int Version { get; }

    public bool CurrentNext { get; }

    /// <summary>The CA descriptors of the program info loop, back to back as broadcast.</summary>
    public ReadOnlyMemory<byte> ProgramCaDescriptors { get; }

    public IReadOnlyList<CaStream> Streams { get; }

    /// <summary>Every ECM pid any CA descriptor names, with the CA system ids that use it.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<int>> EcmPids { get; }

    /// <summary>Whether anything in the program is under conditional access at all.</summary>
    public bool IsScrambled => EcmPids.Count > 0;

    /// <summary>One elementary stream and its own CA descriptors.</summary>
    internal sealed record CaStream(int StreamType, int Pid, ReadOnlyMemory<byte> CaDescriptors);

    /// <exception cref="FormatException">The section is not a program map section, or is cut short.</exception>
    public static CaProgramMap Parse(ReadOnlySpan<byte> section)
    {
        if (section.Length < 16 || section[0] != 0x02)
        {
            throw new FormatException(
                $"Not a program map section: {section.Length} bytes, table id 0x{(section.Length > 0 ? section[0] : 0):X2} (expected 0x02).");
        }

        var sectionLength = ((section[1] & 0x0F) << 8) | section[2];
        var end = 3 + sectionLength - 4; // the CRC is not part of the content
        if (sectionLength < 13 || 3 + sectionLength > section.Length)
        {
            throw new FormatException(
                $"The program map section says it is {3 + sectionLength} bytes long, but {section.Length} bytes were given.");
        }

        var programNumber = BinaryPrimitives.ReadUInt16BigEndian(section[3..]);
        var version = (section[5] >> 1) & 0x1F;
        var currentNext = (section[5] & 1) != 0;
        var programInfoLength = ((section[10] & 0x0F) << 8) | section[11];
        var position = 12;
        if (position + programInfoLength > end)
        {
            throw new FormatException(
                $"The program info of program {programNumber} runs past the end of its section.");
        }

        var ecmPids = new Dictionary<int, List<int>>();
        var programCa = CollectCaDescriptors(section.Slice(position, programInfoLength), ecmPids);
        position += programInfoLength;

        var streams = new List<CaStream>();
        while (position + 5 <= end)
        {
            var streamType = section[position];
            var pid = BinaryPrimitives.ReadUInt16BigEndian(section[(position + 1)..]) & 0x1FFF;
            var esInfoLength = BinaryPrimitives.ReadUInt16BigEndian(section[(position + 3)..]) & 0x0FFF;
            position += 5;
            if (position + esInfoLength > end)
            {
                throw new FormatException(
                    $"The descriptors of stream pid {pid} in program {programNumber} run past the end of the section.");
            }

            var streamCa = CollectCaDescriptors(section.Slice(position, esInfoLength), ecmPids);
            streams.Add(new CaStream(streamType, pid, streamCa));
            position += esInfoLength;
        }

        return new CaProgramMap(
            programNumber,
            version,
            currentNext,
            programCa,
            streams,
            ecmPids.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<int>)pair.Value));
    }

    /// <summary>The ca_pmt body for the descrambling server, see the remarks for its layout.</summary>
    public byte[] BuildCaPmt(byte listManagement, int slot, int programMapPid, int transportStreamId, int originalNetworkId)
    {
        var device = (byte)DeviceNumber(slot);
        var body = new List<byte>(64 + ProgramCaDescriptors.Length)
        {
            listManagement,
            (byte)(ProgramNumber >> 8),
            (byte)ProgramNumber,
            (byte)(0xC0 | (Version << 1) | (CurrentNext ? 1 : 0)),
        };

        var programInfo = new List<byte>
        {
            DvbApiProtocol.CaPmtCommandOkDescrambling,
            0x81, 0x08, 0, 0, 0, 0,
            (byte)(transportStreamId >> 8), (byte)transportStreamId,
            (byte)(originalNetworkId >> 8), (byte)originalNetworkId,
            0x83, 0x01, device,
            0x84, 0x02, (byte)(programMapPid >> 8), (byte)programMapPid,
            0x85, 0x04,
            (byte)(LiveServiceTypeMask >> 24), (byte)(LiveServiceTypeMask >> 16),
            (byte)(LiveServiceTypeMask >> 8), (byte)LiveServiceTypeMask,
            0x86, 0x01, device,
            0x87, 0x01, device,
        };
        programInfo.AddRange(ProgramCaDescriptors.Span);

        body.Add((byte)(0xF0 | (programInfo.Count >> 8)));
        body.Add((byte)programInfo.Count);
        body.AddRange(programInfo);

        foreach (var stream in Streams)
        {
            body.Add((byte)stream.StreamType);
            body.Add((byte)(0xE0 | (stream.Pid >> 8)));
            body.Add((byte)stream.Pid);
            var esInfoLength = stream.CaDescriptors.Length == 0 ? 0 : 1 + stream.CaDescriptors.Length;
            body.Add((byte)(0xF0 | (esInfoLength >> 8)));
            body.Add((byte)esInfoLength);
            if (esInfoLength > 0)
            {
                body.Add(DvbApiProtocol.CaPmtCommandOkDescrambling);
                body.AddRange(stream.CaDescriptors.Span);
            }
        }

        return [.. body];
    }

    private static byte[] CollectCaDescriptors(ReadOnlySpan<byte> descriptors, Dictionary<int, List<int>> ecmPids)
    {
        var collected = new List<byte>();
        var position = 0;
        while (position + 2 <= descriptors.Length)
        {
            var tag = descriptors[position];
            var length = descriptors[position + 1];
            if (position + 2 + length > descriptors.Length)
            {
                break;
            }

            if (tag == 0x09 && length >= 4)
            {
                var caSystemId = BinaryPrimitives.ReadUInt16BigEndian(descriptors[(position + 2)..]);
                var caPid = BinaryPrimitives.ReadUInt16BigEndian(descriptors[(position + 4)..]) & 0x1FFF;
                if (!ecmPids.TryGetValue(caPid, out var systems))
                {
                    ecmPids[caPid] = systems = [];
                }

                if (!systems.Contains(caSystemId))
                {
                    systems.Add(caSystemId);
                }

                collected.AddRange(descriptors.Slice(position, 2 + length));
            }

            position += 2 + length;
        }

        return [.. collected];
    }
}
