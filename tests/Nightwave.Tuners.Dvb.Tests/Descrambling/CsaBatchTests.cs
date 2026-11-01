using System.Diagnostics;
using System.Globalization;
using Nightwave.Tuners.Dvb.Descrambling.Csa;
using Xunit.Abstractions;

namespace Nightwave.Tuners.Dvb.Tests.Descrambling;

/// <summary>
/// The batched CSA against the reference: whatever the batch looks like, the bytes that come out
/// have to be the same.
/// </summary>
public class CsaBatchTests(ITestOutputHelper output)
{
    [Fact]
    public void A_full_batch_matches_the_published_vectors()
    {
        var key = CsaKey.Create(Convert.FromHexString("0706050403020100"));
        var buffer = new byte[184 * CsaBatchDescrambler.LaneCount];
        var payloads = new CsaPayload[CsaBatchDescrambler.LaneCount];
        for (var i = 0; i < payloads.Length; i++)
        {
            payloads[i] = new CsaPayload(i * 184, 184);
            for (var j = 0; j < 184; j++)
            {
                buffer[(i * 184) + j] = (byte)j;
            }
        }

        new CsaBatchDescrambler().Descramble(key, buffer, payloads);

        var expected = new byte[184];
        for (var j = 0; j < 184; j++)
        {
            expected[j] = (byte)j;
        }

        CsaReference.Descramble(key, expected);
        for (var i = 0; i < payloads.Length; i++)
        {
            Assert.Equal(expected, buffer.AsSpan(i * 184, 184).ToArray());
        }
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(5, 0)]
    [InlineData(6, 0)]
    [InlineData(7, 4)]
    [InlineData(64, 0)]
    [InlineData(127, 4)]
    [InlineData(128, 0)]
    [InlineData(129, 4)]
    [InlineData(300, 0)]
    public void Random_payloads_of_every_length_match_the_reference(int count, byte ecmMode)
    {
        var random = new Random(count * 31);
        var cw = new byte[8];
        random.NextBytes(cw);
        var key = CsaKey.Create(cw, ecmMode);

        var payloads = new CsaPayload[count];
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            // Every length from nothing to a full packet turns up: adaptation fields make short
            // payloads, and the residue after the last block is anything from 0 to 7 bytes.
            var length = i % 3 == 0 ? 184 : random.Next(0, 185);
            payloads[i] = new CsaPayload(offset + 4, length);
            offset += 188;
        }

        var buffer = new byte[offset];
        random.NextBytes(buffer);
        var expected = buffer.ToArray();
        foreach (var payload in payloads)
        {
            CsaReference.Descramble(key, expected.AsSpan(payload.Offset, payload.Length));
        }

        new CsaBatchDescrambler().Descramble(key, buffer, payloads);

        Assert.Equal(expected, buffer);
    }

    [Fact]
    public void Bytes_outside_the_payloads_are_not_touched()
    {
        var key = CsaKey.Create([1, 2, 3, 4, 5, 6, 7, 8]);
        var buffer = new byte[188 * 10];
        Array.Fill(buffer, (byte)0x47);
        var payloads = Enumerable.Range(0, 10).Select(i => new CsaPayload((i * 188) + 4, 184)).ToArray();

        new CsaBatchDescrambler().Descramble(key, buffer, payloads);

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal([0x47, 0x47, 0x47, 0x47], buffer.AsSpan(i * 188, 4).ToArray());
        }
    }

    /// <summary>
    /// Every instruction set this processor has, the portable one always among them, by name:
    /// the enum is internal.
    /// </summary>
    public static TheoryData<string> Paths()
    {
        var paths = new TheoryData<string>();
        foreach (var path in Enum.GetValues<CsaVectorPath>())
        {
            if (CsaBitMatrix.IsSupported(path))
            {
                paths.Add(path.ToString());
            }
        }

        return paths;
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void Every_path_matches_the_reference_for_every_batch_size(string pathName)
    {
        var path = Enum.Parse<CsaVectorPath>(pathName);
        var random = new Random(17);
        var descrambler = new CsaBatchDescrambler(path);
        for (var count = 1; count <= 300; count++)
        {
            var cw = new byte[8];
            random.NextBytes(cw);
            var key = CsaKey.Create(cw, (byte)(count % 5 == 0 ? CsaKey.IcamMode : 0));

            var payloads = new CsaPayload[count];
            for (var i = 0; i < count; i++)
            {
                // Adaptation fields make payloads of any length: all residues and block counts,
                // short payloads mixed with full ones in the same group of 16.
                var length = i % 4 == 0 ? 184 : random.Next(0, 185);
                payloads[i] = new CsaPayload((i * 188) + 188 - length, length);
            }

            var buffer = new byte[count * 188];
            random.NextBytes(buffer);
            var expected = buffer.ToArray();
            foreach (var payload in payloads)
            {
                CsaReference.Descramble(key, expected.AsSpan(payload.Offset, payload.Length));
            }

            descrambler.Descramble(key, buffer, payloads);

            Assert.True(expected.AsSpan().SequenceEqual(buffer), $"{path}, {count} packets");
        }
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void Every_path_matches_the_reference_for_every_payload_length(string pathName)
    {
        var path = Enum.Parse<CsaVectorPath>(pathName);
        var random = new Random(23);
        var key = CsaKey.Create([0x11, 0x22, 0x33, 0x66, 0x44, 0x55, 0x66, 0xff]);
        var descrambler = new CsaBatchDescrambler(path);

        // One batch holding every length from 0 to 184, and one per length.
        var lengths = Enumerable.Range(0, 185).ToArray();
        foreach (var batchLengths in lengths.Select(l => new[] { l, l, 184 - l }).Prepend(lengths))
        {
            var payloads = batchLengths.Select((length, i) => new CsaPayload((i * 188) + 4, length)).ToArray();
            var buffer = new byte[batchLengths.Length * 188];
            random.NextBytes(buffer);
            var expected = buffer.ToArray();
            foreach (var payload in payloads)
            {
                CsaReference.Descramble(key, expected.AsSpan(payload.Offset, payload.Length));
            }

            descrambler.Descramble(key, buffer, payloads);

            Assert.True(expected.AsSpan().SequenceEqual(buffer), $"{path}, lengths {string.Join(',', batchLengths.Take(3))}");
        }
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void Every_path_descrambles_the_published_vectors(string pathName)
    {
        var path = Enum.Parse<CsaVectorPath>(pathName);
        var key = CsaKey.Create(Convert.FromHexString("07e01b02c9e045ee"));
        var clear = Convert.FromHexString(
            "afbefbefbefbefbefbefbefbe6b5ad7cf9f3e5b16c7cf9f3e6b5ad6b5f3e7cf96c5b1f3e7cf9ad6b5ad7cf9f3e5b16c7" +
            "cf9f3e6b5ad6b5f3e7cf96c5b1f3e7cf9ad6b5ad7cf9f3e5b16c7cf9f3e6b5ad6b5f3e7cf96c5b1f3e7cf9ad6b5ad7cf" +
            "9f3e5b16c7cf9f3e6b5ad6b5f3e7cf96c5b1f3e7cf9ad6b5ad7cf9f3e5b16c7cf9f3e6b5ad6b5f3e7cf96c5b1f3e7cf9" +
            "ad6b5ad7cf9f3e5b16c7cf9f3e6b5ad6b5f3e7cf96c5b1f3e7cf9ad000000000fffc440066b11111");
        var scrambled = clear.ToArray();
        CsaReference.Scramble(key, scrambled);

        var buffer = Enumerable.Repeat(scrambled, 37).SelectMany(b => b).ToArray();
        var payloads = Enumerable.Range(0, 37).Select(i => new CsaPayload(i * 184, 184)).ToArray();
        new CsaBatchDescrambler(path).Descramble(key, buffer, payloads);

        for (var i = 0; i < 37; i++)
        {
            Assert.Equal(clear, buffer.AsSpan(i * 184, 184).ToArray());
        }
    }

    [Theory]
    [MemberData(nameof(Paths))]
    public void The_stream_transpose_is_its_own_inverse_and_moves_single_bits(string pathName)
    {
        var path = Enum.Parse<CsaVectorPath>(pathName);
        var random = new Random(3);
        var rows = new System.Runtime.Intrinsics.Vector128<ulong>[64];
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<System.Runtime.Intrinsics.Vector128<ulong>, ulong>(rows.AsSpan());
        for (var i = 0; i < 128; i++)
        {
            words[i] = (ulong)random.NextInt64();
        }

        var original = words.ToArray();
        Transpose(path, rows);
        Transpose(path, rows);
        Assert.Equal(original, words.ToArray());

        // Bit b of packet n (word n) becomes bit n / 2 of row b, in the half n picks.
        for (var n = 0; n < 128; n += 13)
        {
            for (var bit = 0; bit < 64; bit += 7)
            {
                words.Clear();
                words[n] = 1UL << bit;
                Transpose(path, rows);
                var expected = new ulong[128];
                expected[(bit * 2) + (n & 1)] = 1UL << (n / 2);
                Assert.Equal(expected, words.ToArray());
            }
        }
    }

    private static void Transpose(CsaVectorPath path, System.Runtime.Intrinsics.Vector128<ulong>[] rows)
    {
        if (path == CsaVectorPath.Arm64)
        {
            CsaBatchStreamCipher.Transpose64<CsaArm64Swaps>(rows);
        }
        else
        {
            CsaBatchStreamCipher.Transpose64<CsaPortableSwaps>(rows);
        }
    }

    /// <summary>
    /// Throughput on this machine, single core. Not a pass or fail: run it by hand with
    /// <c>dotnet test --filter "FullyQualifiedName~Throughput"</c> after removing the skip, and
    /// read the numbers from the test output.
    /// </summary>
    [Theory(Skip = "Benchmark, run by hand.")]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(32)]
    [InlineData(128)]
    [InlineData(348)]
    [InlineData(1024)]
    public void Throughput(int packetsPerCall)
    {
        var key = CsaKey.Create([0x11, 0x22, 0x33, 0x66, 0x44, 0x55, 0x66, 0xff]);
        var buffer = new byte[188 * packetsPerCall];
        new Random(1).NextBytes(buffer);
        var payloads = Enumerable.Range(0, packetsPerCall).Select(i => new CsaPayload((i * 188) + 4, 184)).ToArray();
        var descrambler = new CsaBatchDescrambler();

        for (var i = 0; i < 20; i++)
        {
            descrambler.Descramble(key, buffer, payloads);
        }

        var packets = 0L;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(3))
        {
            descrambler.Descramble(key, buffer, payloads);
            packets += packetsPerCall;
        }

        var seconds = stopwatch.Elapsed.TotalSeconds;
        var megabits = packets * 188 * 8 / seconds / 1e6;

        var referencePackets = 0L;
        var referenceBuffer = buffer.AsSpan(4, 184).ToArray();
        stopwatch.Restart();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(1))
        {
            CsaReference.Descramble(key, referenceBuffer);
            referencePackets++;
        }

        var referenceMegabits = referencePackets * 188 * 8 / stopwatch.Elapsed.TotalSeconds / 1e6;
        output.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{packetsPerCall} packets per call: {megabits:F0} Mbit/s batched ({descrambler.Path}), {referenceMegabits:F1} Mbit/s reference"));
    }
}
