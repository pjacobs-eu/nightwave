using Nightwave.Tuners.Dvb.Descrambling;

namespace Nightwave.Tuners.Dvb.Tests.Descrambling;

public class CaProgramMapTests
{
    [Fact]
    public void A_program_map_yields_its_ecm_pids_and_streams()
    {
        var map = CaProgramMap.Parse(Ts.ProgramMap(caid: 0x0963, version: 3));

        Assert.Equal(Ts.ServiceId, map.ProgramNumber);
        Assert.Equal(3, map.Version);
        Assert.True(map.IsScrambled);
        Assert.Equal([0x0963], map.EcmPids[Ts.EcmPid]);
        Assert.Equal([Ts.VideoPid, Ts.AudioPid], map.Streams.Select(stream => stream.Pid));
    }

    /// <summary>
    /// The CA PMT as the server reads it: list management, program, version, program info with the
    /// command id, the private descriptors (0x81 tsid/onid, 0x83 adapter, 0x84 pmt pid, 0x86
    /// demux, 0x87 ca device - all three the slot's device number, 23 - slot) and the program's CA descriptor, then the streams. Non-CA stream
    /// descriptors are left out, and streams without CA descriptors have no command id.
    /// </summary>
    [Fact]
    public void The_ca_pmt_has_the_layout_the_server_reads()
    {
        var map = CaProgramMap.Parse(Ts.ProgramMap(caid: 0x0963, version: 1));

        var caPmt = map.BuildCaPmt(DvbApiProtocol.ListManagementAdd, slot: 5, programMapPid: Ts.ProgramMapPid, transportStreamId: 0x0441, originalNetworkId: 0x0085);

        var expected = Convert.FromHexString(
            "04" + "2B66" + "C3" + "F024"
            + "01"
            + "8108000000000441" + "0085"
            + "830112"
            + "84020100"
            + "8504" + "00000001"
            + "860112"
            + "870112"
            + "09040963E500"
            + "1BE101F000"
            + "03E102F000");
        Assert.Equal(expected, caPmt);
    }

    /// <summary>
    /// Other dvbapi clients number their services from 0, and the server keeps its descrambler
    /// bookkeeping per ca device across all clients: the slots take the numbers from 23 down.
    /// </summary>
    [Theory]
    [InlineData(0, 23)]
    [InlineData(1, 22)]
    [InlineData(2, 21)]
    [InlineData(23, 0)]
    public void Slots_take_the_device_numbers_from_the_top(int slot, int device)
    {
        Assert.Equal(device, CaProgramMap.DeviceNumber(slot));
        Assert.Equal(slot, CaProgramMap.SlotOfDevice(device));

        var caPmt = CaProgramMap.Parse(Ts.ProgramMap()).BuildCaPmt(DvbApiProtocol.ListManagementAdd, slot, Ts.ProgramMapPid, 1, 2);
        Assert.Equal([0x83, 0x01, (byte)device], caPmt[17..20]);
        Assert.Equal([0x86, 0x01, (byte)device, 0x87, 0x01, (byte)device], caPmt[30..36]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(32)]
    [InlineData(255)]
    public void Adapter_numbers_outside_the_slots_belong_to_no_slot(int device) =>
        Assert.Equal(-1, CaProgramMap.SlotOfDevice(device));

    [Fact]
    public void Stream_level_ca_descriptors_go_with_their_stream()
    {
        var map = CaProgramMap.Parse(Ts.ProgramMap(caOnStream: true, withAudio: false));

        var caPmt = map.BuildCaPmt(DvbApiProtocol.ListManagementUpdate, 0, Ts.ProgramMapPid, 1, 2);

        Assert.Equal(DvbApiProtocol.ListManagementUpdate, caPmt[0]);
        Assert.Equal([0x0963], map.EcmPids[Ts.EcmPid]);
        Assert.Equal(Convert.FromHexString("1BE101F007" + "01" + "09040963E500"), caPmt[^12..]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00B00D0001C100000000E100F000")]
    [InlineData("02B0FF2B66C10000E101F000")]
    public void Anything_else_is_refused_with_the_reason(string hex)
    {
        var exception = Assert.Throws<FormatException>(() => CaProgramMap.Parse(Convert.FromHexString(hex)));
        Assert.False(string.IsNullOrWhiteSpace(exception.Message));
    }
}
