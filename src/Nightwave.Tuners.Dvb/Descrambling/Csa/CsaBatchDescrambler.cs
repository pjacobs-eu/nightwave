using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nightwave.Tuners.Dvb.Descrambling.Csa;

/// <summary>Where one scrambled payload sits in the buffer handed to <see cref="CsaBatchDescrambler"/>.</summary>
internal readonly record struct CsaPayload(int Offset, int Length);

/// <summary>
/// DVB-CSA for many packets at once, which is the only way it is fast enough: a few HD
/// channels on four slow cores.
/// </summary>
/// <remarks>
/// <para>
/// Payloads are taken 128 at a time and copied into a buffer of their own, 184 bytes apart, so
/// that both halves of the cipher can read and write whole 8 byte blocks without minding where
/// a payload ends. Then the stream cipher runs bitsliced (<see cref="CsaBatchStreamCipher"/>)
/// and the block cipher byte-sliced (<see cref="CsaBatchBlockCipher"/>), the design of
/// libdvbcsa, and the payloads are copied back.
/// </para>
/// <para>
/// The bitsliced stream cipher costs as much for one packet as for 128, and still beats running
/// <see cref="CsaReference"/> on a single one by a factor of three. The block cipher of small
/// batches runs packet by packet on 64 bit integers instead, see <see cref="MinimumBlockBatch"/>.
/// </para>
/// <para>
/// One instance per descrambled service: it keeps scratch buffers and is not thread safe.
/// </para>
/// </remarks>
internal sealed class CsaBatchDescrambler
{
    public const int LaneCount = 128;

    /// <summary>Distance between two payloads in the batch buffer: the longest payload there is.</summary>
    public const int PacketStride = MaxPayload;

    /// <summary>
    /// Below this many packets the block cipher runs packet by packet: with fewer than 16 there
    /// is one group of the byte-sliced cipher, and its 56 rounds are one long chain of lookups
    /// where the packet by packet cipher has eight blocks in flight. Measured on an Apple M4:
    /// 0.7 microseconds per packet alone, 8 for a group.
    /// </summary>
    public const int MinimumBlockBatch = 12;

    private const int MaxPayload = 184;

    /// <summary>Bytes 0, 2, 3 and 4: where the decryption round mixes L into the block.</summary>
    private const ulong LMix = 0x0000_0001_0101_0001UL;

    /// <summary>
    /// For each S-box input: the S-box output mixed into bytes 0, 2, 3 and 4 and its permutation
    /// into byte 6, the part of a decryption round that depends on the lookup.
    /// </summary>
    private static readonly ulong[] RoundTable = BuildRoundTable();

    private readonly CsaVectorPath _path;
    private readonly CsaBatchStreamCipher _stream = new();
    private readonly CsaBatchBlockCipher _block;

    // Scratch, reused between batches.
    private readonly byte[] _packets = new byte[LaneCount * PacketStride];
    private readonly int[] _blocksLengths = new int[LaneCount];

    public CsaBatchDescrambler()
        : this(CsaBitMatrix.BestPath)
    {
    }

    /// <summary>A descrambler on a chosen instruction set, for the tests to hold the paths against each other.</summary>
    internal CsaBatchDescrambler(CsaVectorPath path)
    {
        if (!CsaBitMatrix.IsSupported(path))
        {
            throw new PlatformNotSupportedException($"The {path} path of the batched CSA does not run on this processor.");
        }

        _path = path;
        _block = new CsaBatchBlockCipher(path);
    }

    public CsaVectorPath Path => _path;

    /// <summary>
    /// Descrambles every payload in place with <paramref name="key"/>. Payloads shorter than
    /// 8 bytes are left alone, as the standard has it.
    /// </summary>
    public void Descramble(CsaKey key, Span<byte> buffer, ReadOnlySpan<CsaPayload> payloads)
    {
        Span<CsaPayload> batch = stackalloc CsaPayload[LaneCount];
        var count = 0;
        foreach (var payload in payloads)
        {
            if (payload.Length < 8)
            {
                continue;
            }

            if (payload.Length > MaxPayload)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(payloads),
                    $"A transport stream payload is at most {MaxPayload} bytes, this one is {payload.Length}.");
            }

            batch[count++] = payload;
            if (count == LaneCount)
            {
                DescrambleBatch(key, buffer, batch);
                count = 0;
            }
        }

        if (count > 0)
        {
            DescrambleBatch(key, buffer, batch[..count]);
        }
    }

    private void DescrambleBatch(CsaKey key, Span<byte> buffer, ReadOnlySpan<CsaPayload> batch)
    {
        var longest = 0;
        var packets = _packets.AsSpan();
        var blocksLengths = _blocksLengths.AsSpan(0, batch.Length);
        for (var n = 0; n < batch.Length; n++)
        {
            var payload = batch[n];
            buffer.Slice(payload.Offset, payload.Length).CopyTo(packets.Slice(n * PacketStride, PacketStride));
            blocksLengths[n] = payload.Length & ~7;
            longest = Math.Max(longest, payload.Length);
        }

        switch (_path)
        {
            case CsaVectorPath.Arm64:
                Run<CsaArm64Swaps>(key, batch.Length, longest);
                break;
            default:
                Run<CsaPortableSwaps>(key, batch.Length, longest);
                break;
        }

        for (var n = 0; n < batch.Length; n++)
        {
            var payload = batch[n];
            packets.Slice(n * PacketStride, payload.Length).CopyTo(buffer.Slice(payload.Offset, payload.Length));
        }
    }

    private void Run<TSwaps>(CsaKey key, int count, int longest)
        where TSwaps : struct, ICsaSwaps
    {
        _stream.XorKeystreams<TSwaps>(key.ControlWord, _packets, count, longest);
        if (count < MinimumBlockBatch)
        {
            for (var n = 0; n < count; n++)
            {
                DecryptBlocks(key.BlockSchedule, _packets.AsSpan(n * PacketStride, _blocksLengths[n]));
            }
        }
        else
        {
            _block.Decrypt<TSwaps>(key.BlockSchedule, _packets, _blocksLengths.AsSpan(0, count), longest & ~7);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Small batches: the block cipher packet by packet, on 64 bit integers
    // ---------------------------------------------------------------------------------------

    private static ulong[] BuildRoundTable()
    {
        var table = new ulong[256];
        for (var input = 0; input < 256; input++)
        {
            ulong s = CsaTables.BlockSbox[input];
            table[input] = (s * LMix) ^ ((ulong)CsaTables.BlockPermutation[(int)s] << 48);
        }

        return table;
    }

    /// <summary>
    /// The decryption round of <see cref="CsaReference.DecryptBlock"/> on a little-endian 64 bit
    /// block: shifting by 8 moves every byte one up, and the rest XORs in L and perm(S).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Round(ulong w, byte key, ref ulong table) =>
        (w << 8) ^ ((w >> 56) * LMix) ^ Unsafe.Add(ref table, (byte)(key ^ (w >> 48)));

    /// <summary>
    /// The blocks of one payload, eight at a time: they decrypt independently, only the final XOR
    /// chains them, so the processor has independent work while each lookup is in flight.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void DecryptBlocks(ReadOnlySpan<byte> schedule, Span<byte> payload)
    {
        var blocks = payload.Length / 8;
        Span<ulong> cipher = stackalloc ulong[MaxPayload / 8];
        Span<ulong> plain = stackalloc ulong[MaxPayload / 8];
        for (var i = 0; i < blocks; i++)
        {
            cipher[i] = BinaryPrimitives.ReadUInt64LittleEndian(payload[(i * 8)..]);
        }

        ref var table = ref MemoryMarshal.GetArrayDataReference(RoundTable);
        ref var keys = ref MemoryMarshal.GetReference(schedule);

        var k = 0;
        for (; k + 8 <= blocks; k += 8)
        {
            var w0 = cipher[k];
            var w1 = cipher[k + 1];
            var w2 = cipher[k + 2];
            var w3 = cipher[k + 3];
            var w4 = cipher[k + 4];
            var w5 = cipher[k + 5];
            var w6 = cipher[k + 6];
            var w7 = cipher[k + 7];
            for (var round = CsaTables.BlockRounds - 1; round >= 0; round--)
            {
                var roundKey = Unsafe.Add(ref keys, round);
                w0 = Round(w0, roundKey, ref table);
                w1 = Round(w1, roundKey, ref table);
                w2 = Round(w2, roundKey, ref table);
                w3 = Round(w3, roundKey, ref table);
                w4 = Round(w4, roundKey, ref table);
                w5 = Round(w5, roundKey, ref table);
                w6 = Round(w6, roundKey, ref table);
                w7 = Round(w7, roundKey, ref table);
            }

            plain[k] = w0;
            plain[k + 1] = w1;
            plain[k + 2] = w2;
            plain[k + 3] = w3;
            plain[k + 4] = w4;
            plain[k + 5] = w5;
            plain[k + 6] = w6;
            plain[k + 7] = w7;
        }

        for (; k + 4 <= blocks; k += 4)
        {
            var w0 = cipher[k];
            var w1 = cipher[k + 1];
            var w2 = cipher[k + 2];
            var w3 = cipher[k + 3];
            for (var round = CsaTables.BlockRounds - 1; round >= 0; round--)
            {
                var roundKey = Unsafe.Add(ref keys, round);
                w0 = Round(w0, roundKey, ref table);
                w1 = Round(w1, roundKey, ref table);
                w2 = Round(w2, roundKey, ref table);
                w3 = Round(w3, roundKey, ref table);
            }

            plain[k] = w0;
            plain[k + 1] = w1;
            plain[k + 2] = w2;
            plain[k + 3] = w3;
        }

        for (; k < blocks; k++)
        {
            var w = cipher[k];
            for (var round = CsaTables.BlockRounds - 1; round >= 0; round--)
            {
                w = Round(w, Unsafe.Add(ref keys, round), ref table);
            }

            plain[k] = w;
        }

        for (k = 0; k < blocks; k++)
        {
            var chained = k + 1 < blocks ? plain[k] ^ cipher[k + 1] : plain[k];
            BinaryPrimitives.WriteUInt64LittleEndian(payload[(k * 8)..], chained);
        }
    }
}
