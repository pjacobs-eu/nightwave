using Nightwave.Tuners.Dvb.Descrambling;
using Nightwave.Tuners.Dvb.Descrambling.Csa;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nightwave.Tuners.Dvb.Tests.Descrambling;

/// <summary>
/// What the service does with packets and with the server's messages, without a network: messages
/// are handed to it the way the connection's reader would.
/// </summary>
public sealed class ServiceDescramblerTests : IAsyncDisposable
{
    private static readonly byte[] EvenWord = [0x11, 0x22, 0x33, 0x66, 0x44, 0x55, 0x66, 0xFF];
    private static readonly byte[] OddWord = [0xA1, 0xB2, 0xC3, 0x16, 0xD4, 0xE5, 0xF6, 0xBF];
    private static readonly byte[] NextEvenWord = [0x01, 0x02, 0x03, 0x06, 0x04, 0x05, 0x06, 0x0F];

    private readonly DvbApiClient _client;
    private readonly DvbApiServiceDescrambler _service;

    public ServiceDescramblerTests()
    {
        _client = new DvbApiClient(
            new DvbApiOptions { Host = "127.0.0.1", Port = 1 },
            DvbApiTimings.Default,
            NullLogger.Instance,
            TimeProvider.System);
        _service = new DvbApiServiceDescrambler(
            new ServiceDescramblingRequest
            {
                ServiceId = Ts.ServiceId,
                ProgramMapSection = Ts.ProgramMap(),
                ProgramMapPid = Ts.ProgramMapPid,
                Slot = 3,
                TransportStreamId = 1,
                OriginalNetworkId = 2,
                ChannelName = "Test channel",
            },
            CaProgramMap.Parse(Ts.ProgramMap()),
            _client,
            NullLogger.Instance,
            TimeProvider.System);
    }

    public async ValueTask DisposeAsync() => await _client.DisposeAsync();

    private void Keys(byte[]? even = null, byte[]? odd = null, int index = 0)
    {
        _service.OnMessage(new DvbApiSetPid(3, Ts.VideoPid, index));
        _service.OnMessage(new DvbApiSetPid(3, Ts.AudioPid, index));
        if (even is not null)
        {
            _service.OnMessage(new DvbApiSetDescrambler(3, index, 0, even));
        }

        if (odd is not null)
        {
            _service.OnMessage(new DvbApiSetDescrambler(3, index, 1, odd));
        }
    }

    /// <summary>A packet that starts a PES unit, scrambled with <paramref name="key"/>.</summary>
    private static byte[] ScrambledUnitStart(CsaKey key, bool odd)
    {
        var packet = new byte[188];

        packet[0] = 0x47;
        packet[1] = 0x40 | (Ts.VideoPid >> 8);
        packet[2] = Ts.VideoPid & 0xFF;
        packet[3] = (byte)((odd ? 0xC0 : 0x80) | 0x10);
        new byte[] { 0, 0, 1, 0xE0 }.CopyTo(packet, 4);

        for (var at = 8; at < packet.Length; at++)
        {
            packet[at] = (byte)(at * 7);
        }

        CsaReference.Scramble(key, packet.AsSpan(4));

        return packet;
    }

    [Fact]
    public void A_key_is_tried_on_a_copy_and_fits_only_the_packets_it_scrambled()
    {
        Keys(even: EvenWord, odd: OddWord);

        var fitting = ScrambledUnitStart(CsaKey.Create(OddWord), odd: true);
        var before = fitting.ToArray();

        Assert.Equal(KeyProbe.Fits, _service.Probe(fitting));
        Assert.Equal(before, fitting);

        // The key of the period before, still in its place: exactly what a late key looks like.
        Assert.Equal(KeyProbe.Wrong, _service.Probe(ScrambledUnitStart(CsaKey.Create(NextEvenWord), odd: false)));
    }

    [Fact]
    public void Without_a_key_of_its_parity_a_packet_does_not_fit()
    {
        Keys(even: EvenWord);

        Assert.Equal(KeyProbe.Wrong, _service.Probe(ScrambledUnitStart(CsaKey.Create(OddWord), odd: true)));
    }

    [Fact]
    public void A_packet_with_too_little_payload_says_nothing_about_its_key()
    {
        Keys(even: EvenWord);

        var (packet, _) = Ts.ScrambledPacket(Ts.VideoPid, CsaKey.Create(EvenWord), odd: false, continuity: 0, adaptationLength: 170);

        Assert.Equal(KeyProbe.NotApplicable, _service.Probe(packet));
    }

    [Fact]
    public void Process_is_forwarding_and_descrambling_in_one()
    {
        Keys(even: EvenWord);

        var (first, clear) = Ts.ScrambledPacket(Ts.VideoPid, CsaKey.Create(EvenWord), odd: false, continuity: 0);
        var second = first.ToArray();

        _service.Process(first);
        _service.ForwardSections(second);
        _service.Descramble(second);

        Assert.Equal(clear, first);
        Assert.Equal(clear, second);
    }

    [Fact]
    public void Without_a_key_scrambled_packets_pass_unchanged()
    {
        var (scrambled, _) = Ts.ScrambledPacket(Ts.VideoPid, CsaKey.Create(EvenWord), odd: false, continuity: 0);
        var buffer = scrambled.ToArray();

        _service.Process(buffer);

        Assert.Equal(scrambled, buffer);
        Assert.False(_service.HasKeys);
    }

    [Fact]
    public void Even_and_odd_packets_use_their_own_key_and_lose_their_flags()
    {
        Keys(EvenWord, OddWord);
        var even = Ts.ScrambledPacket(Ts.VideoPid, CsaKey.Create(EvenWord), odd: false, continuity: 0);
        var odd = Ts.ScrambledPacket(Ts.AudioPid, CsaKey.Create(OddWord), odd: true, continuity: 1);
        byte[] buffer = [.. even.Scrambled, .. odd.Scrambled];

        _service.Process(buffer);

        Assert.True(_service.HasKeys);
        Assert.Equal([.. even.Clear, .. odd.Clear], buffer);
    }

    /// <summary>
    /// The next even key arrives while odd packets are still on air: they keep the odd key, and
    /// the even packets that follow use the new one.
    /// </summary>
    [Fact]
    public void A_new_key_only_applies_to_packets_of_its_parity()
    {
        Keys(EvenWord, OddWord);
        _service.OnMessage(new DvbApiSetDescrambler(3, 0, 0, NextEvenWord));

        var odd = Ts.ScrambledPacket(Ts.VideoPid, CsaKey.Create(OddWord), odd: true, continuity: 0);
        var even = Ts.ScrambledPacket(Ts.VideoPid, CsaKey.Create(NextEvenWord), odd: false, continuity: 1);
        byte[] buffer = [.. odd.Scrambled, .. even.Scrambled];

        _service.Process(buffer);

        Assert.Equal([.. odd.Clear, .. even.Clear], buffer);
    }

    [Theory]
    [InlineData(0)]   // adaptation field of just its length byte
    [InlineData(7)]   // payload of 176 bytes
    [InlineData(100)] // 83 bytes: 10 blocks and a residue of 3
    [InlineData(176)] // 7 bytes: shorter than a block, not scrambled at all
    [InlineData(182)] // payload of a single byte
    [InlineData(183)] // no payload
    public void Adaptation_fields_move_the_payload(int adaptationLength)
    {
        Keys(EvenWord);
        var packet = Ts.ScrambledPacket(Ts.VideoPid, CsaKey.Create(EvenWord), odd: false, continuity: 0, adaptationLength: adaptationLength);
        var buffer = packet.Scrambled.ToArray();

        _service.Process(buffer);

        Assert.Equal(packet.Clear, buffer);
    }

    [Fact]
    public void A_mix_of_packet_shapes_in_one_call_all_come_out_clear()
    {
        Keys(EvenWord, OddWord);
        var expected = new List<byte>();
        var input = new List<byte>();
        for (var i = 0; i < 300; i++)
        {
            var odd = i % 7 < 3;
            var packet = Ts.ScrambledPacket(
                i % 2 == 0 ? Ts.VideoPid : Ts.AudioPid,
                CsaKey.Create(odd ? OddWord : EvenWord),
                odd,
                continuity: i,
                adaptationLength: i % 5 == 0 ? i % 180 : -1,
                seed: i * 13);
            input.AddRange(packet.Scrambled);
            expected.AddRange(packet.Clear);
        }

        var buffer = input.ToArray();
        _service.Process(buffer);

        Assert.Equal(expected.ToArray(), buffer);
    }

    [Fact]
    public void Clear_packets_are_left_alone()
    {
        Keys(EvenWord);
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = Ts.VideoPid >> 8;
        packet[2] = Ts.VideoPid & 0xFF;
        packet[3] = 0x10;
        new Random(1).NextBytes(packet.AsSpan(4));
        var buffer = packet.ToArray();

        _service.Process(buffer);

        Assert.Equal(packet, buffer);
    }

    [Fact]
    public void A_pid_the_server_switched_off_stays_scrambled()
    {
        Keys(EvenWord);
        _service.OnMessage(new DvbApiSetPid(3, Ts.AudioPid, -1));
        var packet = Ts.ScrambledPacket(Ts.AudioPid, CsaKey.Create(EvenWord), odd: false, continuity: 0);
        var buffer = packet.Scrambled.ToArray();

        _service.Process(buffer);

        Assert.Equal(packet.Scrambled, buffer);
    }

    [Fact]
    public void Required_pids_follow_the_servers_filters()
    {
        var changes = 0;
        _service.RequiredPidsChanged += (_, _) => changes++;

        _service.OnMessage(new DvbApiSetFilter(3, 0, 0, Ts.EcmPid, [0x80, .. new byte[15]], [0xF0, .. new byte[15]], new byte[16], 0, 0));
        _service.OnMessage(new DvbApiSetFilter(3, 0, 1, 0x0001, [0x01, .. new byte[15]], [0xFF, .. new byte[15]], new byte[16], 0, 0));
        Assert.Equal([0x0001, Ts.EcmPid], _service.RequiredPids);

        // A second filter on the same pid changes nothing the tuner has to know.
        _service.OnMessage(new DvbApiSetFilter(3, 0, 2, Ts.EcmPid, [0x81, .. new byte[15]], [0xFF, .. new byte[15]], new byte[16], 0, 0));
        Assert.Equal(2, changes);

        _service.OnMessage(new DvbApiStopFilter(3, 0, 1, 0x0001));
        Assert.Equal([Ts.EcmPid], _service.RequiredPids);
        Assert.Equal(3, changes);
    }

    /// <summary>
    /// iCAM end to end: the ECM that is forwarded announces mode 4, and the control word the
    /// server then sends for that parity is scheduled accordingly.
    /// </summary>
    [Fact]
    public void An_icam_ecm_switches_its_parity_to_the_icam_key_schedule()
    {
        _service.OnMessage(new DvbApiSetFilter(3, 0, 0, Ts.EcmPid, [0x80, .. new byte[15]], [0xFE, .. new byte[15]], new byte[16], 0, 0));
        var continuity = 0;
        _service.Process(Ts.Packets(Ts.EcmPid, ref continuity, Ts.Ecm(0x81, 1, icamMode: CsaKey.IcamMode)));

        Keys(odd: OddWord);
        var icam = Ts.ScrambledPacket(Ts.VideoPid, CsaKey.Create(OddWord, CsaKey.IcamMode), odd: true, continuity: 0);
        var buffer = icam.Scrambled.ToArray();
        _service.Process(buffer);

        Assert.Equal(icam.Clear, buffer);
        Assert.Contains("iCAM", _service.Status.Algorithm, StringComparison.Ordinal);
        Assert.Equal(1, _service.Status.EcmSectionsForwarded);
    }

    [Fact]
    public void A_plain_ecm_keeps_plain_csa()
    {
        _service.OnMessage(new DvbApiSetFilter(3, 0, 0, Ts.EcmPid, [0x80, .. new byte[15]], [0xFE, .. new byte[15]], new byte[16], 0, 0));
        var continuity = 0;
        _service.Process(Ts.Packets(Ts.EcmPid, ref continuity, Ts.Ecm(0x80, 1)));

        Keys(even: EvenWord);
        var plain = Ts.ScrambledPacket(Ts.VideoPid, CsaKey.Create(EvenWord), odd: false, continuity: 0);
        var buffer = plain.Scrambled.ToArray();
        _service.Process(buffer);

        Assert.Equal(plain.Clear, buffer);
        Assert.Equal("CSA", _service.Status.Algorithm);
    }

    [Fact]
    public void An_unknown_icam_mode_is_reported()
    {
        _service.OnMessage(new DvbApiSetFilter(3, 0, 0, Ts.EcmPid, [0x80, .. new byte[15]], [0xFE, .. new byte[15]], new byte[16], 0, 0));
        var continuity = 0;
        _service.Process(Ts.Packets(Ts.EcmPid, ref continuity, Ts.Ecm(0x80, 1, icamMode: 7)));

        Assert.Equal(DescramblerErrors.UnsupportedIcamModeCode, _service.Status.Problem?.Code);
    }

    /// <summary>DVB-CISSA: AES-128-CBC, the IV restarting with every packet, the residue in the clear.</summary>
    [Fact]
    public void Aes_cbc_keys_from_the_extended_api_descramble()
    {
        var key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var iv = "DVBTMCPTAESCISSA"u8.ToArray();
        _service.OnMessage(new DvbApiSetPid(3, Ts.VideoPid, 1));
        _service.OnMessage(new DvbApiSetDescramblerMode(3, 1, DvbApiSetDescramblerMode.AlgorithmAes128, DvbApiSetDescramblerMode.ModeCbc));
        _service.OnMessage(new DvbApiSetDescramblerData(3, 1, 0, DvbApiSetDescramblerData.TypeIv, iv));
        _service.OnMessage(new DvbApiSetDescramblerData(3, 1, 0, DvbApiSetDescramblerData.TypeKey, key));

        var clear = new byte[188];
        clear[0] = 0x47;
        clear[1] = Ts.VideoPid >> 8;
        clear[2] = Ts.VideoPid & 0xFF;
        clear[3] = 0x10;
        new Random(5).NextBytes(clear.AsSpan(4));
        var scrambled = clear.ToArray();
        using (var aes = System.Security.Cryptography.Aes.Create())
        {
            aes.Key = key;
            var encrypted = aes.EncryptCbc(clear.AsSpan(4, 176), iv, System.Security.Cryptography.PaddingMode.None);
            encrypted.CopyTo(scrambled, 4);
        }

        scrambled[3] |= 0x80;
        _service.Process(scrambled);

        Assert.Equal(clear, scrambled);
        Assert.Equal("AES-128-CBC", _service.Status.Algorithm);
    }

    /// <summary>
    /// How the server hands out iCAM with <c>extended_cw_api</c> on, as Sky Deutschland arrived
    /// from a real server, version 2.26: CA_SET_DESCR_MODE with algorithm 3 (CSA-ALT) and a CBC
    /// that means nothing.
    /// The control word that follows is scheduled for iCAM whatever this side read from the ECM.
    /// </summary>
    [Fact]
    public void Csa_alt_from_the_server_descrambles_with_the_icam_key_schedule()
    {
        _service.OnMessage(new DvbApiSetDescramblerMode(3, 0, DvbApiSetDescramblerMode.AlgorithmCsaAlt, DvbApiSetDescramblerMode.ModeCbc));
        Keys(even: EvenWord);

        var icam = Ts.ScrambledPacket(Ts.VideoPid, CsaKey.Create(EvenWord, CsaKey.IcamMode), odd: false, continuity: 0);
        var buffer = icam.Scrambled.ToArray();
        _service.Process(buffer);

        Assert.Equal(icam.Clear, buffer);
        Assert.True(_service.HasKeys);
        Assert.Null(_service.Status.Problem);
        Assert.Contains("iCAM", _service.Status.Algorithm, StringComparison.Ordinal);
    }

    /// <summary>
    /// What RTL HD arrived as, from a real server answering out of its cache: a mode ahead of
    /// every control word, with a cipher mode that changes between them. The key of the other
    /// parity has to survive it - it is the one the broadcaster switches to next, and losing it
    /// froze the picture for a whole crypto period at every other change.
    /// </summary>
    [Fact]
    public void A_mode_before_a_new_control_word_keeps_the_key_of_the_other_parity()
    {
        _service.OnMessage(new DvbApiSetDescramblerMode(3, 0, DvbApiSetDescramblerMode.AlgorithmCsa, DvbApiSetDescramblerMode.ModeEcb));
        Keys(even: EvenWord);
        _service.OnMessage(new DvbApiSetDescramblerMode(3, 0, DvbApiSetDescramblerMode.AlgorithmCsa, DvbApiSetDescramblerMode.ModeCbc));
        Keys(odd: OddWord);

        var even = Ts.ScrambledPacket(Ts.VideoPid, CsaKey.Create(EvenWord), odd: false, continuity: 0);
        var odd = Ts.ScrambledPacket(Ts.VideoPid, CsaKey.Create(OddWord), odd: true, continuity: 1);
        var evenBuffer = even.Scrambled.ToArray();
        var oddBuffer = odd.Scrambled.ToArray();
        _service.Process(evenBuffer);
        _service.Process(oddBuffer);

        Assert.Equal(even.Clear, evenBuffer);
        Assert.Equal(odd.Clear, oddBuffer);
    }

    /// <summary>
    /// A bad answer in the middle of a programme - material a CSA key cannot be made of - keeps
    /// the key that was working instead of turning the picture into noise.
    /// </summary>
    [Fact]
    public void A_control_word_that_makes_no_key_keeps_the_last_good_one()
    {
        Keys(even: EvenWord);
        _service.OnMessage(new DvbApiSetDescramblerData(3, 0, 0, DvbApiSetDescramblerData.TypeKey, [1, 2, 3, 4, 5]));

        var plain = Ts.ScrambledPacket(Ts.VideoPid, CsaKey.Create(EvenWord), odd: false, continuity: 0);
        var buffer = plain.Scrambled.ToArray();
        _service.Process(buffer);

        Assert.Equal(plain.Clear, buffer);
        Assert.True(_service.HasKeys);
    }

    /// <summary>
    /// A control word under an algorithm nobody implements is no key. It used to count as the
    /// first one, which let a channel open on scrambled packets.
    /// </summary>
    [Fact]
    public void A_control_word_that_makes_no_key_is_not_a_key()
    {
        _service.OnMessage(new DvbApiSetDescramblerMode(3, 0, 7, DvbApiSetDescramblerMode.ModeEcb));
        Keys(even: EvenWord);

        Assert.False(_service.HasKeys);
        var problem = _service.Status.Problem;
        Assert.Equal(DescramblerErrors.UnsupportedAlgorithmCode, problem?.Code);
        Assert.Contains("algorithm 7", problem!.Message, StringComparison.Ordinal);
    }
}
