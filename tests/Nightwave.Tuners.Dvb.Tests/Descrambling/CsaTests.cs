using Nightwave.Tuners.Dvb.Descrambling.Csa;

namespace Nightwave.Tuners.Dvb.Tests.Descrambling;

/// <summary>
/// DVB-CSA against known answers first, and then the fast implementation against the simple
/// one on everything the fast one does differently: batches of every size, payloads of every
/// length, keys that change within a batch.
/// </summary>
/// <remarks>
/// The two full-packet vectors are the known-answer vectors every CSA implementation is held to:
/// they ship with libdvbcsa (VideoLAN, test/testdec.c, https://code.videolan.org/videolan/libdvbcsa),
/// the first a real scrambled MPEG video payload, the second a synthetic one. The short-payload
/// and iCAM vectors were produced with the iCAM-patched fork of libdvbcsa
/// (https://github.com/OpenDelta-Team/libdvbcsa, <c>dvbcsa_bs_key_set_ecm</c> and
/// <c>dvbcsa_bs_decrypt</c>, commit f25351d).
/// </remarks>
public class CsaTests
{
    private const string VideoControlWord = "07e01b02c9e045ee";

    private const string VideoScrambled =
        "decf0a0db2d7c440de5d63185a9817aac9bc27c6cb494048fd20b7055b27cbeb9af0ac456d56f47b6fa057f39bf7a2c7" +
        "d46824002f28139694a87cf46f072a0ee8a1ebc780ac1f79bf5db6107c2e52e9342ca83901730424a81edb5bcb24f631" +
        "ab026bf9f6f7e952adcf620f42f6665dc086f27b4020a9bd1ffd16ad2e75a6a085f39c31204efb956178ce10c1485fd3" +
        "610512f4e204aee086015655b10fa633952092f0be3931e12af793b4f7e4f185ae50f163d45d9c6c";

    private const string VideoClear =
        "afbefbefbefbefbefbefbefbe6b5ad7cf9f3e5b16c7cf9f3e6b5ad6b5f3e7cf96c5b1f3e7cf9ad6b5ad7cf9f3e5b16c7" +
        "cf9f3e6b5ad6b5f3e7cf96c5b1f3e7cf9ad6b5ad7cf9f3e5b16c7cf9f3e6b5ad6b5f3e7cf96c5b1f3e7cf9ad6b5ad7cf" +
        "9f3e5b16c7cf9f3e6b5ad6b5f3e7cf96c5b1f3e7cf9ad6b5ad7cf9f3e5b16c7cf9f3e6b5ad6b5f3e7cf96c5b1f3e7cf9" +
        "ad6b5ad7cf9f3e5b16c7cf9f3e6b5ad6b5f3e7cf96c5b1f3e7cf9ad000000000fffc440066b11111";

    private const string CountingControlWord = "0706050403020100";

    private const string CountingClear =
        "2d0a472018119c8ad12a656b89e4352bc2b59061d17e02e13f4670cf77912f2293c16cfe49ad7cc2af861ba329beaa64" +
        "f022b95e98aa60efdfd64477e6bfbb94b20a630e5cf2acb449cc9e4f944c3012e855c244a452cb6181c9b6a66befafa6" +
        "711d7b582ffad10c079d1f3587be029f20c6608f1c300f96d071d65110df5bf6442f8028b7ec23594b940b9a74a11ff7" +
        "9e76b4dfbb3c8c88972256731605acf94f779d38a06b05d2e61501b15cc962a99b1a6a1acfe6a8ba";

    /// <summary>The counting payload descrambled with the iCAM key schedule (ECM mode 4).</summary>
    private const string CountingClearIcam =
        "e368a84a594ed462827c8cf08e720974679de42a6667dc3e7f70a6ff9cf9fffeccc44aebb47e542422d922c016d0400c" +
        "0667138afa19d21f0ad90e90370d168bd60e6dcc8afb9c4766b42b21e9e209c4dfa9e93d8c6920f144e586640228e7b7" +
        "17d8dc285545dfe1f4f954290e22aec2b965039a57c185877bcac3b508aa541be1af0a3ce20e7c84eab7910bd2f64d91" +
        "3d9493350a86a94d040a84694329b92e7d7b4b55e982c2753bb3c23da5d71849d57f75d900d9ceaa";

    /// <summary>101 bytes <c>(i * 7 + 3) mod 256</c>, descrambled: twelve blocks and a five byte residue.</summary>
    private const string ShortClear =
        "0df1624af15a9dbc144d8604e5940ceceab68075f9c403d9e4f6dd6a02b393eac62c0bc589b443d3465719b126b741ba" +
        "513ad2230d81d3a1e83249b82da6bd433f636e7d68a7f621535464797d0a0d07764f30eea75df4670618db95be2d5417" +
        "801c33e86d";

    private static byte[] Counting(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)i)];

    private static byte[] ShortScrambled() => [.. Enumerable.Range(0, 101).Select(i => (byte)((i * 7) + 3))];

    [Fact]
    public void The_reference_descrambles_the_published_vectors()
    {
        var video = Convert.FromHexString(VideoScrambled);
        CsaReference.Descramble(CsaKey.Create(Convert.FromHexString(VideoControlWord)), video);
        Assert.Equal(VideoClear, Convert.ToHexStringLower(video));

        var counting = Counting(184);
        CsaReference.Descramble(CsaKey.Create(Convert.FromHexString(CountingControlWord)), counting);
        Assert.Equal(CountingClear, Convert.ToHexStringLower(counting));
    }

    [Fact]
    public void The_reference_leaves_the_residue_to_the_stream_cipher()
    {
        var payload = ShortScrambled();
        CsaReference.Descramble(CsaKey.Create(Convert.FromHexString(CountingControlWord)), payload);

        Assert.Equal(ShortClear, Convert.ToHexStringLower(payload));
    }

    [Fact]
    public void The_reference_follows_the_icam_key_schedule()
    {
        var payload = Counting(184);
        CsaReference.Descramble(CsaKey.Create(Convert.FromHexString(CountingControlWord), CsaKey.IcamMode), payload);

        Assert.Equal(CountingClearIcam, Convert.ToHexStringLower(payload));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(0x15)]
    public void Any_ecm_mode_but_four_is_plain_csa(byte ecmMode)
    {
        var payload = Counting(184);
        CsaReference.Descramble(CsaKey.Create(Convert.FromHexString(CountingControlWord), ecmMode), payload);

        Assert.Equal(CountingClear, Convert.ToHexStringLower(payload));
    }

    /// <summary>The published iCAM table (<c>csa_block_perm_ecm</c>), rows 0, 1 and 15.</summary>
    [Fact]
    public void The_icam_bit_permutation_is_the_published_table()
    {
        byte[] firstRow = [0x00, 0x02, 0x80, 0x82, 0x20, 0x22, 0xa0, 0xa2, 0x04, 0x06, 0x84, 0x86, 0x24, 0x26, 0xa4, 0xa6];
        byte[] secondRow = [0x40, 0x42, 0xc0, 0xc2, 0x60, 0x62, 0xe0, 0xe2, 0x44, 0x46, 0xc4, 0xc6, 0x64, 0x66, 0xe4, 0xe6];
        byte[] lastRow = [0x59, 0x5b, 0xd9, 0xdb, 0x79, 0x7b, 0xf9, 0xfb, 0x5d, 0x5f, 0xdd, 0xdf, 0x7d, 0x7f, 0xfd, 0xff];

        Assert.Equal(firstRow, CsaTables.IcamPermutation[..16]);
        Assert.Equal(secondRow, CsaTables.IcamPermutation[16..32]);
        Assert.Equal(lastRow, CsaTables.IcamPermutation[240..]);
    }

    [Fact]
    public void Scrambling_is_undone_by_descrambling()
    {
        var random = new Random(7);
        foreach (var length in new[] { 8, 9, 15, 16, 17, 100, 183, 184 })
        {
            var clear = new byte[length];
            random.NextBytes(clear);
            var cw = new byte[8];
            random.NextBytes(cw);
            var key = CsaKey.Create(cw, CsaKey.IcamMode);

            var payload = clear.ToArray();
            CsaReference.Scramble(key, payload);
            Assert.NotEqual(clear, payload);

            CsaReference.Descramble(key, payload);
            Assert.Equal(clear, payload);
        }
    }

    [Fact]
    public void Scrambling_reproduces_the_published_cipher_text()
    {
        var payload = Convert.FromHexString(VideoClear);
        CsaReference.Scramble(CsaKey.Create(Convert.FromHexString(VideoControlWord)), payload);

        Assert.Equal(VideoScrambled, Convert.ToHexStringLower(payload));
    }

    [Fact]
    public void Payloads_shorter_than_a_block_are_not_scrambled()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5, 6, 7 };
        CsaReference.Descramble(CsaKey.Create(Convert.FromHexString(CountingControlWord)), payload);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7 }, payload);
    }

    [Fact]
    public void The_truth_tables_are_the_sbox_tables()
    {
        foreach (var sbox in CsaTables.StreamSboxes)
        {
            for (var index = 0; index < 32; index++)
            {
                Assert.Equal(sbox.Table[index] & 1, (int)(sbox.LowTruthTable >> index) & 1);
                Assert.Equal(sbox.Table[index] >> 1, (int)(sbox.HighTruthTable >> index) & 1);
            }
        }
    }
}
