using Nightwave.Tuners.Dvb.Descrambling;

namespace Nightwave.Tuners.Dvb.Tests.Descrambling;

/// <summary>
/// The dvbapi messages byte for byte, written out by hand from the reference implementation's
/// module-dvbapi.c rather than produced by the code under test.
/// </summary>
public class DvbApiProtocolTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty, StringComparison.Ordinal));

    [Fact]
    public void The_greeting_is_opcode_version_and_name_never_prefixed()
    {
        Assert.Equal(Hex("FFFF0001 0002 09 4E6967687477617665"), DvbApiProtocol.EncodeClientInfo(2, "Nightwave"));
        Assert.Equal(Hex("FFFF0001 0003 09 4E6967687477617665"), DvbApiProtocol.EncodeClientInfo(3, "Nightwave"));
    }

    [Fact]
    public void A_ca_pmt_carries_its_asn1_length()
    {
        Assert.Equal(Hex("9F8032 03 040506"), DvbApiProtocol.EncodeCaPmt(2, 9, [4, 5, 6]));

        var medium = DvbApiProtocol.EncodeCaPmt(2, 9, new byte[0x80]);
        Assert.Equal(Hex("9F8032 8180"), medium[..5]);
        Assert.Equal(5 + 0x80, medium.Length);

        var large = DvbApiProtocol.EncodeCaPmt(2, 9, new byte[0x123]);
        Assert.Equal(Hex("9F8032 820123"), large[..6]);
        Assert.Equal(6 + 0x123, large.Length);
    }

    [Fact]
    public void Version_three_prefixes_every_message_with_start_byte_and_id()
    {
        Assert.Equal(Hex("A5 00000009 9F8032 01 04"), DvbApiProtocol.EncodeCaPmt(3, 9, [4]));
        Assert.Equal(Hex("A5 0000000A 9F803F04 830200 07"), DvbApiProtocol.EncodeCaStop(3, 10, 7));
        Assert.Equal(Hex("A5 0000000B FFFF0000 02 05 807000"), DvbApiProtocol.EncodeFilterData(3, 11, 2, 5, [0x80, 0x70, 0x00]));
    }

    [Fact]
    public void Version_two_has_no_prefix()
    {
        Assert.Equal(Hex("9F803F04 830200 1F"), DvbApiProtocol.EncodeCaStop(2, 10, 31));
        Assert.Equal(Hex("FFFF0000 02 05 807000"), DvbApiProtocol.EncodeFilterData(2, 11, 2, 5, [0x80, 0x70, 0x00]));
    }

    public static TheoryData<string, int, object> ServerMessages => new()
    {
        {
            "FFFF0002 0003 0F 436172645365727665722031312E30",
            2,
            new DvbApiServerInfo(3, "CardServer 11.0")
        },
        {
            "A5 00000000 FFFF0002 0003 00",
            3,
            new DvbApiServerInfo(3, string.Empty)
        },
        {
            "40086F87 04 00000101 00000002",
            2,
            new DvbApiSetPid(4, 0x101, 2)
        },
        {
            "40086F87 04 00000101 FFFFFFFF",
            2,
            new DvbApiSetPid(4, 0x101, -1)
        },
        {
            "400C6F88 01 00000003 00000002 00000001",
            2,
            new DvbApiSetDescramblerMode(1, 3, 2, 1)
        },
        {
            "00006F2A 01 02 03 0500",
            2,
            new DvbApiStopFilter(1, 2, 3, 0x500)
        },
        {
            "A5 0000002A 00006F2A 01 02 03 0500",
            3,
            new DvbApiStopFilter(1, 2, 3, 0x500) { MessageId = 42 }
        },
    };

    [Theory]
    [MemberData(nameof(ServerMessages))]
    public void Server_messages_decode(string hex, int version, object expected)
    {
        var bytes = Hex(hex);

        Assert.True(DvbApiProtocol.TryDecode(bytes, version, out var message, out var consumed));
        Assert.Equal(expected, message);
        Assert.Equal(bytes.Length, consumed);
    }

    [Fact]
    public void A_control_word_decodes_with_index_and_parity()
    {
        var bytes = Hex("40106F86 02 00000001 00000001 1122334455667788");

        Assert.True(DvbApiProtocol.TryDecode(bytes, 2, out var message, out var consumed));
        var descrambler = Assert.IsType<DvbApiSetDescrambler>(message);
        Assert.Equal((2, 1, 1), (descrambler.Adapter, descrambler.Index, descrambler.Parity));
        Assert.Equal(Hex("1122334455667788"), descrambler.ControlWord);
        Assert.Equal(bytes.Length, consumed);
    }

    [Fact]
    public void A_filter_decodes_with_pid_data_mask_and_flags()
    {
        var bytes = Hex(
            "403C6F2B 00 03 01 0500"
            + "80000000000000000000000000000000"
            + "F0000000000000000000000000000000"
            + "00000000000000000000000000000000"
            + "00000000 00000004");

        Assert.True(DvbApiProtocol.TryDecode(bytes, 2, out var message, out var consumed));
        var filter = Assert.IsType<DvbApiSetFilter>(message);
        Assert.Equal((0, 3, 1, 0x500, 4u), (filter.Adapter, filter.Demux, filter.Filter, filter.Pid, filter.Flags));
        Assert.Equal(0x80, filter.Data[0]);
        Assert.Equal(0xF0, filter.Mask[0]);
        Assert.Equal(bytes.Length, consumed);
    }

    [Fact]
    public void Ecm_info_decodes_its_strings()
    {
        var bytes = Hex(
            "FFFF0003 00 2B66 0963 0500 00000000 0000007B"
            + "0B 766964656F6775617264" + "32"
            + "04 63617264"
            + "05 6C6F63616C"
            + "08 696E7465726E616C"
            + "00");

        Assert.True(DvbApiProtocol.TryDecode(bytes, 2, out var message, out var consumed));
        var info = Assert.IsType<DvbApiEcmInfo>(message);
        Assert.Equal(0x0963, info.CaId);
        Assert.Equal(0x500, info.Pid);
        Assert.Equal(123u, info.EcmTimeMilliseconds);
        Assert.Equal("videoguard2", info.CardSystem);
        Assert.Equal("card", info.Reader);
        Assert.Equal("local", info.Source);
        Assert.Equal("internal", info.Protocol);
        Assert.Equal(bytes.Length, consumed);
    }

    [Fact]
    public void Aes_key_data_decodes_with_its_length()
    {
        var bytes = Hex("40186F89 00 00000001 00000000 00000000 00000010 44564254 4D435054 41455343 49535341");

        Assert.True(DvbApiProtocol.TryDecode(bytes, 2, out var message, out _));
        var data = Assert.IsType<DvbApiSetDescramblerData>(message);
        Assert.Equal(DvbApiSetDescramblerData.TypeIv, data.DataType);
        Assert.Equal("DVBTMCPTAESCISSA"u8.ToArray(), data.Data);
    }

    [Fact]
    public void Every_cut_short_message_waits_for_more()
    {
        var bytes = Hex("A5 00000001 40106F86 02 00000001 00000001 1122334455667788");
        for (var length = 0; length < bytes.Length; length++)
        {
            Assert.False(DvbApiProtocol.TryDecode(bytes.AsSpan(0, length), 3, out _, out var consumed));
            Assert.Equal(0, consumed);
        }
    }

    [Fact]
    public void Two_messages_in_one_buffer_decode_one_after_the_other()
    {
        var bytes = Hex("40086F87 04 00000101 00000002 00006F2A 01 02 03 0500");

        Assert.True(DvbApiProtocol.TryDecode(bytes, 2, out var first, out var consumed));
        Assert.IsType<DvbApiSetPid>(first);
        Assert.True(DvbApiProtocol.TryDecode(bytes.AsSpan(consumed), 2, out var second, out _));
        Assert.IsType<DvbApiStopFilter>(second);
    }

    [Fact]
    public void An_unknown_opcode_ends_the_stream()
    {
        Assert.Throws<DvbApiProtocolException>(() => DvbApiProtocol.TryDecode(Hex("12345678 00"), 2, out _, out _));
    }

    [Fact]
    public void A_missing_start_byte_in_version_three_ends_the_stream()
    {
        Assert.Throws<DvbApiProtocolException>(() => DvbApiProtocol.TryDecode(Hex("40086F87 04 00000101 00000002"), 3, out _, out _));
    }
}
