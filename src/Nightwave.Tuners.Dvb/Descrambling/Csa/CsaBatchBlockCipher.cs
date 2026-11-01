// Ported from libdvbcsa (src/dvbcsa_bs_block.c, src/dvbcsa_bs_transpose_block.c,
// src/dvbcsa_bs_sse.h), as published in https://github.com/OpenDelta-Team/libdvbcsa at commit
// f25351d70589ec97ae6fb7590786c9aa38b999d4:
//
//   Based on FFdecsa, Copyright (C) 2003-2004  fatih89r
//   (c) 2006-2008 Alexandre Becoulet <alexandre.becoulet@free.fr>
//   (optimisations by glenvt18)
//
//   libdvbcsa is free software; you can redistribute it and/or modify it under the terms of the
//   GNU General Public License as published by the Free Software Foundation; either version 2 of
//   the License, or (at your option) any later version.
//
// Used here under the GNU General Public License version 3 as part of a work licensed under the
// GNU Affero General Public License version 3 or later.
//
// The 64 bit ARM S-box lookup with tbl/tbx and the grouping of 16 packets per vector, which lets
// a batch skip the groups it does not fill, are not in libdvbcsa.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace Nightwave.Tuners.Dvb.Descrambling.Csa;

/// <summary>
/// The block cipher of <see cref="CsaReference.DecryptBlock"/> for up to 128 packets at once,
/// byte-sliced: each 128 bit vector holds the same byte of the same block of 16 packets.
/// </summary>
/// <remarks>
/// <para>
/// The block cipher's state is 8 bytes and every round looks up one byte in an 8 bit S-box. With
/// byte <c>k</c> of 128 packets spread over eight vectors (<c>8k + g</c> for group <c>g</c> of 16
/// packets), a round is one S-box lookup per packet and a few XOR per vector, and the state
/// "shifts" by moving a pointer: 56 rounds walk down a window of 64 rows.
/// </para>
/// <para>
/// The S-box lookup is where the paths differ, see <see cref="CsaVectorPath"/>. Group <c>g</c>
/// holds packets <c>16g</c> to <c>16g + 15</c>, so a batch of <c>n</c> packets only runs the
/// first <c>ceil(n / 16)</c> groups.
/// </para>
/// <para>
/// Descrambling runs over the packet buffer of <see cref="CsaBatchDescrambler"/> after the stream
/// cipher, one block position at a time, and chains the way libdvbcsa does: when block
/// <c>i</c> is decrypted, block <c>i - 1</c> is XORed with block <c>i</c> as it still is (the
/// cipher text that chained it), and the decrypted block takes its place.
/// </para>
/// </remarks>
internal sealed class CsaBatchBlockCipher
{
    private const int Rounds = CsaTables.BlockRounds;

    /// <summary>For each input byte: the S-box output in the low byte, its bit permutation in the high one.</summary>
    private static readonly ushort[] SboxPermutation = BuildSboxPermutation();

    /// <summary>The window: 8 rows of 8 vectors for the state, and 56 more for it to shift into.</summary>
    private readonly Vector128<ulong>[] _registers = new Vector128<ulong>[8 * (8 + Rounds)];

    /// <summary>Scalar lookups: the S-box inputs of one round.</summary>
    private readonly Vector128<ulong>[] _lookupIn = new Vector128<ulong>[8];

    /// <summary>
    /// Scalar lookups: the S-box outputs and their permutations of one round, interleaved (the
    /// SSSE3 path) or the outputs in the first 8 vectors and the permutations in the next 8.
    /// </summary>
    private readonly Vector128<ulong>[] _lookupOut = new Vector128<ulong>[16];

    private readonly Vector128<byte>[] _sboxTable = BuildSboxTable();

    private readonly CsaVectorPath _path;

    public CsaBatchBlockCipher(CsaVectorPath path)
    {
        _path = path;
    }

    /// <param name="lengths">The payload lengths, rounded down to whole blocks.</param>
    /// <param name="maxBlocksLength">The longest of <paramref name="lengths"/>.</param>
    public void Decrypt<TSwaps>(ReadOnlySpan<byte> schedule, byte[] packets, ReadOnlySpan<int> lengths, int maxBlocksLength)
        where TSwaps : struct, ICsaSwaps
    {
        var groups = (lengths.Length + 15) / 16;
        for (var offset = 0; offset < maxBlocksLength; offset += 8)
        {
            TransposeIn<TSwaps>(packets, lengths.Length, offset, groups);
            switch (_path)
            {
                case CsaVectorPath.Arm64:
                    DecryptRegistersArm64(schedule, groups);
                    break;
                case CsaVectorPath.Ssse3:
                    DecryptRegistersScalar(schedule, groups, interleaved: true);
                    break;
                default:
                    DecryptRegistersScalar(schedule, groups, interleaved: false);
                    break;
            }

            TransposeOutAndXor<TSwaps>(packets, lengths, offset, groups);
        }
    }

    // ---------------------------------------------------------------------------------------
    // Transposes
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Where the low half of packet <c>n</c>'s block goes among the 32 bit words of the first 32
    /// rows (the high half 128 words further): row <c>g + 8t</c>, word <c>c</c> for
    /// <c>n = 16g + 4t + c</c>. The 4x4 byte transposes then gather the 16 packets of group
    /// <c>g</c> into rows <c>8k + g</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WordOf(int packet) => (4 * ((packet >> 4) + (8 * ((packet >> 2) & 3)))) + (packet & 3);

    /// <summary><c>dvbcsa_bs_block_transpose_in</c>, into the last 8 rows of the window.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void TransposeIn<TSwaps>(byte[] packets, int count, int offset, int groups)
        where TSwaps : struct, ICsaSwaps
    {
        var rows = _registers.AsSpan(8 * Rounds, 64);
        var words = MemoryMarshal.Cast<Vector128<ulong>, uint>(rows);
        ref var packet = ref packets[offset];
        for (var n = 0; n < count; n++)
        {
            var block = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref packet, n * CsaBatchDescrambler.PacketStride));
            var word = WordOf(n);
            words[word] = (uint)block;
            words[word + 128] = (uint)(block >> 32);
        }

        TransposeBlock<TSwaps>(rows, groups);
    }

    /// <summary>
    /// <c>dvbcsa_bs_block_transpose_out_and_xor</c>, from the first 8 rows of the window: the
    /// decrypted block to its place, and the block before it XORed with what was there.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void TransposeOutAndXor<TSwaps>(byte[] packets, ReadOnlySpan<int> lengths, int offset, int groups)
        where TSwaps : struct, ICsaSwaps
    {
        var rows = _registers.AsSpan(0, 64);
        TransposeBlock<TSwaps>(rows, groups);

        var words = MemoryMarshal.Cast<Vector128<ulong>, uint>(rows);
        ref var packet = ref packets[offset];
        for (var n = 0; n < lengths.Length; n++)
        {
            if (offset >= lengths[n])
            {
                continue;
            }

            var word = WordOf(n);
            var decrypted = words[word] | ((ulong)words[word + 128] << 32);
            ref var block = ref Unsafe.Add(ref packet, n * CsaBatchDescrambler.PacketStride);
            if (offset > 0)
            {
                ref var previous = ref Unsafe.Subtract(ref block, 8);
                Unsafe.WriteUnaligned(ref previous, Unsafe.ReadUnaligned<ulong>(ref previous) ^ Unsafe.ReadUnaligned<ulong>(ref block));
            }

            Unsafe.WriteUnaligned(ref block, decrypted);
        }
    }

    /// <summary>
    /// <c>dvbcsa_bs_matrix_transpose_block</c>: a 4x4 byte transpose across rows
    /// <c>g, g + 8, g + 16, g + 24</c> within every 32 bit word, for both halves. Its own inverse.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TransposeBlock<TSwaps>(Span<Vector128<ulong>> rows, int groups)
        where TSwaps : struct, ICsaSwaps
    {
        ref var r = ref MemoryMarshal.GetReference(rows);
        _ = rows[63];
        for (var j = 0; j < 64; j += 32)
        {
            for (var i = 0; i < groups; i++)
            {
                ref var s = ref Unsafe.Add(ref r, j + i);
                var a0 = s;
                var a1 = Unsafe.Add(ref s, 8);
                var a2 = Unsafe.Add(ref s, 16);
                var a3 = Unsafe.Add(ref s, 24);

                TSwaps.Swap8(ref a0, ref a1);
                TSwaps.Swap8(ref a2, ref a3);
                TSwaps.Swap16(ref a0, ref a2);
                TSwaps.Swap16(ref a1, ref a3);

                s = a0;
                Unsafe.Add(ref s, 8) = a1;
                Unsafe.Add(ref s, 16) = a2;
                Unsafe.Add(ref s, 24) = a3;
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // Rounds
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// <c>dvbcsa_bs_block_decrypt_register</c> with the S-box in registers: <c>tbl</c> on the
    /// first 64 entries, <c>tbx</c> on the other three quarters with the index XORed into range
    /// (an index out of range leaves the byte alone), and the bit permutation as two 16 entry
    /// lookups, one per nibble.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void DecryptRegistersArm64(ReadOnlySpan<byte> schedule, int groups)
    {
        if (!AdvSimd.Arm64.IsSupported)
        {
            throw new PlatformNotSupportedException("The ARM64 path of the CSA block cipher runs on 64 bit ARM only.");
        }

        var t = _sboxTable;
        var quarter0 = (t[0], t[1], t[2], t[3]);
        var quarter1 = (t[4], t[5], t[6], t[7]);
        var quarter2 = (t[8], t[9], t[10], t[11]);
        var quarter3 = (t[12], t[13], t[14], t[15]);
        var permutationLow = t[16];
        var permutationHigh = t[17];
        var select1 = Vector128.Create((byte)0x40);
        var select2 = Vector128.Create((byte)0x80);
        var select3 = Vector128.Create((byte)0xc0);
        var lowNibble = Vector128.Create((byte)0x0f);

        ref var r = ref MemoryMarshal.GetArrayDataReference(_registers);
        ref var keys = ref MemoryMarshal.GetReference(schedule);
        _ = schedule[Rounds - 1];

        var row = 8 * Rounds;
        for (var i = Rounds - 1; i >= 0; i--)
        {
            row -= 8;
            ref var w = ref Unsafe.Add(ref r, row);
            var key = Vector128.Create(Unsafe.Add(ref keys, i));
            for (var g = 0; g < groups; g++)
            {
                ref var cell = ref Unsafe.Add(ref w, g);
                var x = key ^ Unsafe.Add(ref cell, 8 * 7).AsByte();

                var sbox =
                    (AdvSimd.Arm64.VectorTableLookup(quarter0, x) | AdvSimd.Arm64.VectorTableLookup(quarter1, x ^ select1))
                    | (AdvSimd.Arm64.VectorTableLookup(quarter2, x ^ select2) | AdvSimd.Arm64.VectorTableLookup(quarter3, x ^ select3));
                var permuted =
                    AdvSimd.Arm64.VectorTableLookup(permutationLow, sbox & lowNibble)
                    | AdvSimd.Arm64.VectorTableLookup(permutationHigh, AdvSimd.ShiftRightLogical(sbox, 4));

                Round(ref cell, sbox.AsUInt64(), permuted.AsUInt64());
            }
        }
    }

    /// <summary>
    /// <c>dvbcsa_bs_block_decrypt_register</c> with the S-box looked up byte by byte from a
    /// combined S-box and permutation table (libdvbcsa's <c>dvbcsa_block_sbox_perm</c>). SSSE3
    /// splits the 16 bit results with <c>pshufb</c> (<c>BS_LOAD_DEINTERLEAVE_8</c>); without it
    /// the two bytes are stored apart.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void DecryptRegistersScalar(ReadOnlySpan<byte> schedule, int groups, bool interleaved)
    {
        if (interleaved && !Ssse3.IsSupported)
        {
            throw new PlatformNotSupportedException("The SSSE3 path of the CSA block cipher needs SSSE3.");
        }

        ref var r = ref MemoryMarshal.GetArrayDataReference(_registers);
        ref var keys = ref MemoryMarshal.GetReference(schedule);
        _ = schedule[Rounds - 1];
        ref var table = ref MemoryMarshal.GetArrayDataReference(SboxPermutation);
        ref var lookupIn = ref MemoryMarshal.GetArrayDataReference(_lookupIn);
        ref var lookupOut = ref MemoryMarshal.GetArrayDataReference(_lookupOut);
        ref var inBytes = ref Unsafe.As<Vector128<ulong>, byte>(ref lookupIn);
        ref var outBytes = ref Unsafe.As<Vector128<ulong>, byte>(ref lookupOut);
        var bytes = groups * 16;
        var deinterleave = Vector128.Create((byte)0, 2, 4, 6, 8, 10, 12, 14, 1, 3, 5, 7, 9, 11, 13, 15);

        var row = 8 * Rounds;
        for (var i = Rounds - 1; i >= 0; i--)
        {
            row -= 8;
            ref var w = ref Unsafe.Add(ref r, row);
            var key = Vector128.Create(Unsafe.Add(ref keys, i)).AsUInt64();
            for (var g = 0; g < groups; g++)
            {
                Unsafe.Add(ref lookupIn, g) = key ^ Unsafe.Add(ref w, (8 * 7) + g);
            }

            if (interleaved)
            {
                ref var output = ref Unsafe.As<byte, ushort>(ref outBytes);
                for (var j = 0; j < bytes; j += 4)
                {
                    var input = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inBytes, j));
                    Unsafe.Add(ref output, j) = Unsafe.Add(ref table, (int)(input & 0xff));
                    Unsafe.Add(ref output, j + 1) = Unsafe.Add(ref table, (int)((input >> 8) & 0xff));
                    Unsafe.Add(ref output, j + 2) = Unsafe.Add(ref table, (int)((input >> 16) & 0xff));
                    Unsafe.Add(ref output, j + 3) = Unsafe.Add(ref table, (int)(input >> 24));
                }

                for (var g = 0; g < groups; g++)
                {
                    var first = Ssse3.Shuffle(Unsafe.Add(ref lookupOut, 2 * g).AsByte(), deinterleave).AsUInt64();
                    var second = Ssse3.Shuffle(Unsafe.Add(ref lookupOut, (2 * g) + 1).AsByte(), deinterleave).AsUInt64();
                    Round(ref Unsafe.Add(ref w, g), Sse2.UnpackLow(first, second), Sse2.UnpackHigh(first, second));
                }
            }
            else
            {
                for (var j = 0; j < bytes; j += 4)
                {
                    var input = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref inBytes, j));
                    var s0 = Unsafe.Add(ref table, (int)(input & 0xff));
                    var s1 = Unsafe.Add(ref table, (int)((input >> 8) & 0xff));
                    var s2 = Unsafe.Add(ref table, (int)((input >> 16) & 0xff));
                    var s3 = Unsafe.Add(ref table, (int)(input >> 24));
                    Unsafe.WriteUnaligned(
                        ref Unsafe.Add(ref outBytes, j),
                        (uint)((s0 & 0xff) | ((s1 & 0xff) << 8) | ((s2 & 0xff) << 16) | ((s3 & 0xff) << 24)));
                    Unsafe.WriteUnaligned(
                        ref Unsafe.Add(ref outBytes, 128 + j),
                        (uint)((s0 >> 8) | (s1 & 0xff00) | ((s2 & 0xff00) << 8) | ((s3 & 0xff00) << 16)));
                }

                for (var g = 0; g < groups; g++)
                {
                    Round(ref Unsafe.Add(ref w, g), Unsafe.Add(ref lookupOut, g), Unsafe.Add(ref lookupOut, 8 + g));
                }
            }
        }
    }

    /// <summary>
    /// The rest of a decryption round for one group, <paramref name="cell"/> being its byte 0 in
    /// the new position of the window: the new byte 0 is the old byte 7 (now row 8) XOR the S-box
    /// output, which also goes into bytes 2, 3 and 4; the permuted output goes into byte 6.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Round(ref Vector128<ulong> cell, Vector128<ulong> sbox, Vector128<ulong> permuted)
    {
        var w = Unsafe.Add(ref cell, 8 * 8) ^ sbox;
        cell = w;
        Unsafe.Add(ref cell, 8 * 2) ^= w;
        Unsafe.Add(ref cell, 8 * 3) ^= w;
        Unsafe.Add(ref cell, 8 * 4) ^= w;
        Unsafe.Add(ref cell, 8 * 6) ^= permuted;
    }

    // ---------------------------------------------------------------------------------------
    // Tables
    // ---------------------------------------------------------------------------------------

    private static ushort[] BuildSboxPermutation()
    {
        var table = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            var s = CsaTables.BlockSbox[i];
            table[i] = (ushort)(s | (CsaTables.BlockPermutation[s] << 8));
        }

        return table;
    }

    /// <summary>
    /// The S-box as 16 vectors of 16 entries, then the bit permutation of a low nibble and of a
    /// high nibble: the permutation moves bits, so the permutation of a byte is the OR of the two.
    /// </summary>
    private static Vector128<byte>[] BuildSboxTable()
    {
        var table = new Vector128<byte>[18];
        Span<byte> entries = stackalloc byte[16];
        for (var v = 0; v < 16; v++)
        {
            CsaTables.BlockSbox.Slice(v * 16, 16).CopyTo(entries);
            table[v] = Vector128.Create((ReadOnlySpan<byte>)entries);
        }

        for (var n = 0; n < 16; n++)
        {
            entries[n] = CsaTables.BlockPermutation[n];
        }

        table[16] = Vector128.Create((ReadOnlySpan<byte>)entries);
        for (var n = 0; n < 16; n++)
        {
            entries[n] = CsaTables.BlockPermutation[n << 4];
        }

        table[17] = Vector128.Create((ReadOnlySpan<byte>)entries);
        return table;
    }
}
