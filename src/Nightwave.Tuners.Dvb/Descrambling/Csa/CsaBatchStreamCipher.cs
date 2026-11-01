// Ported from libdvbcsa (src/dvbcsa_bs_stream.c, src/dvbcsa_bs_stream_kernel.inc,
// src/dvbcsa_bs_transpose_stream.c, src/dvbcsa_bs_key.c), as published in
// https://github.com/OpenDelta-Team/libdvbcsa at commit f25351d70589ec97ae6fb7590786c9aa38b999d4:
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

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Nightwave.Tuners.Dvb.Descrambling.Csa;

/// <summary>
/// The stream cipher of <see cref="CsaReference.XorKeystream"/> for up to 128 packets at once,
/// bitsliced: every register bit is a 128 bit vector holding that bit for all packets.
/// </summary>
/// <remarks>
/// <para>
/// The cipher works on single bits and 4 bit cells, so bitslicing turns each of its seven small
/// S-boxes into about twenty AND, OR and XOR for all 128 packets together. The S-box formulas,
/// the register window and the 64x64 bit transposes are libdvbcsa's.
/// </para>
/// <para>
/// Works on the packet buffer of <see cref="CsaBatchDescrambler"/>: packet <c>n</c> at
/// <c>n * 184</c>, the 8 initialisation bytes first. Keystream is XORed in 8 bytes at a time,
/// up to the multiple of 8 at or above the longest payload; whatever that writes past a shorter
/// payload's end stays in the buffer.
/// </para>
/// </remarks>
internal sealed class CsaBatchStreamCipher
{
    /// <summary>Clocks between two moves of the register window back to its start.</summary>
    private const int WindowClocks = 32;

    private const int WindowRows = WindowClocks + 10;

    private readonly Vector128<ulong>[] _a = new Vector128<ulong>[WindowRows * 4];
    private readonly Vector128<ulong>[] _b = new Vector128<ulong>[WindowRows * 4];

    /// <summary>
    /// Bit <c>b</c> of byte <c>i</c> of 8 bytes of every packet in row <c>8i + b</c>: the
    /// initialisation bytes going in, 8 keystream bytes coming out.
    /// </summary>
    private readonly Vector128<ulong>[] _rows = new Vector128<ulong>[64];

    private State _state;

    public void XorKeystreams<TSwaps>(ReadOnlySpan<byte> controlWord, byte[] packets, int count, int maxLength)
        where TSwaps : struct, ICsaSwaps
    {
        var rows = MemoryMarshal.Cast<Vector128<ulong>, ulong>(_rows.AsSpan());
        rows.Clear();
        for (var i = 0; i < count; i++)
        {
            rows[i] = Unsafe.ReadUnaligned<ulong>(ref packets[i * CsaBatchDescrambler.PacketStride]);
        }

        Transpose64<TSwaps>(_rows);
        LoadKey(controlWord);
        Clock(initialising: true);

        for (var offset = 8; offset < maxLength; offset += 8)
        {
            Clock(initialising: false);
            Transpose64<TSwaps>(_rows);
            ref var packet = ref packets[offset];
            for (var i = 0; i < count; i++)
            {
                ref var target = ref Unsafe.Add(ref packet, i * CsaBatchDescrambler.PacketStride);
                Unsafe.WriteUnaligned(ref target, Unsafe.ReadUnaligned<ulong>(ref target) ^ rows[i]);
            }
        }
    }

    /// <summary>
    /// <c>dvbcsa_bs_key_set</c> and the start of <c>dvbcsa_bs_stream_cipher_batch</c>: the
    /// control word's 64 bits into cells A1 to A8 and B1 to B8, the same in every lane.
    /// </summary>
    private void LoadKey(ReadOnlySpan<byte> controlWord)
    {
        var ck = BinaryPrimitives.ReadUInt64LittleEndian(controlWord);
        for (var i = 0; i < 8; i++)
        {
            for (var b = 0; b < 4; b++)
            {
                _a[((WindowClocks + i) * 4) + b] = Bit(ck, (b + (i * 4)) ^ 4);
                _b[((WindowClocks + i) * 4) + b] = Bit(ck, (b + (i * 4) + 32) ^ 4);
            }
        }

        for (var i = 8; i < 10; i++)
        {
            for (var b = 0; b < 4; b++)
            {
                _a[((WindowClocks + i) * 4) + b] = Vector128<ulong>.Zero;
                _b[((WindowClocks + i) * 4) + b] = Vector128<ulong>.Zero;
            }
        }

        _state = default;
    }

    private static Vector128<ulong> Bit(ulong value, int bit) =>
        ((value >> bit) & 1) != 0 ? Vector128<ulong>.AllBitsSet : Vector128<ulong>.Zero;

    /// <summary>
    /// <c>dvbcsa_bs_stream_cipher_kernel</c>: 32 clocks, which is 8 bytes of keystream. While
    /// initialising, the 8 initialisation bytes in <see cref="_rows"/> are fed in and nothing
    /// comes out; after that, the keystream bytes replace them.
    /// </summary>
    // Hot loop: compiled fully optimised straight away rather than through tiering.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Clock(bool initialising)
    {
        ref var sb = ref MemoryMarshal.GetArrayDataReference(_rows);

        // A and B point at cell 1 (libdvbcsa's A[0]) of the current state, cell k at k - 1 rows
        // further on; every clock the new cell goes in the row before, and the pointer follows.
        ref var a = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_a), WindowClocks * 4);
        ref var bRegister = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_b), WindowClocks * 4);

        var x0 = _state.X0; var x1 = _state.X1; var x2 = _state.X2; var x3 = _state.X3;
        var y0 = _state.Y0; var y1 = _state.Y1; var y2 = _state.Y2; var y3 = _state.Y3;
        var z0 = _state.Z0; var z1 = _state.Z1; var z2 = _state.Z2; var z3 = _state.Z3;
        var d0 = _state.D0; var d1 = _state.D1; var d2 = _state.D2; var d3 = _state.D3;
        var e0 = _state.E0; var e1 = _state.E1; var e2 = _state.E2; var e3 = _state.E3;
        var f0 = _state.F0; var f1 = _state.F1; var f2 = _state.F2; var f3 = _state.F3;
        var p = _state.P; var q = _state.Q; var r = _state.R;

        for (var i = 0; i < 8; i++)
        {
            for (var j = 0; j < 4; j++)
            {
                // Four register B bits for T3.
                var extraB3 = Cell(ref bRegister, 2, 0) ^ Cell(ref bRegister, 5, 1) ^ Cell(ref bRegister, 6, 2) ^ Cell(ref bRegister, 8, 3);
                var extraB2 = Cell(ref bRegister, 5, 0) ^ Cell(ref bRegister, 7, 1) ^ Cell(ref bRegister, 2, 3) ^ Cell(ref bRegister, 3, 2);
                var extraB1 = Cell(ref bRegister, 4, 3) ^ Cell(ref bRegister, 7, 2) ^ Cell(ref bRegister, 3, 0) ^ Cell(ref bRegister, 4, 1);
                var extraB0 = Cell(ref bRegister, 8, 2) ^ Cell(ref bRegister, 5, 3) ^ Cell(ref bRegister, 2, 1) ^ Cell(ref bRegister, 7, 0);

                // T1: the new A cell.
                var nextA0 = Cell(ref a, 9, 0) ^ x0;
                var nextA1 = Cell(ref a, 9, 1) ^ x1;
                var nextA2 = Cell(ref a, 9, 2) ^ x2;
                var nextA3 = Cell(ref a, 9, 3) ^ x3;

                // T2: the new B cell, rotated left by one where p is set.
                var nextB0 = Cell(ref bRegister, 6, 0) ^ Cell(ref bRegister, 9, 0) ^ y0;
                var nextB1 = Cell(ref bRegister, 6, 1) ^ Cell(ref bRegister, 9, 1) ^ y1;
                var nextB2 = Cell(ref bRegister, 6, 2) ^ Cell(ref bRegister, 9, 2) ^ y2;
                var nextB3 = Cell(ref bRegister, 6, 3) ^ Cell(ref bRegister, 9, 3) ^ y3;

                if (initialising)
                {
                    // The initialisation byte goes in high nibble to A and low to B, then the
                    // other way round, twice.
                    ref var toA = ref Unsafe.Add(ref sb, (8 * i) + ((j & 1) != 0 ? 0 : 4));
                    ref var toB = ref Unsafe.Add(ref sb, (8 * i) + ((j & 1) != 0 ? 4 : 0));
                    nextA0 ^= d0 ^ toA;
                    nextA1 ^= d1 ^ Unsafe.Add(ref toA, 1);
                    nextA2 ^= d2 ^ Unsafe.Add(ref toA, 2);
                    nextA3 ^= d3 ^ Unsafe.Add(ref toA, 3);
                    nextB0 ^= toB;
                    nextB1 ^= Unsafe.Add(ref toB, 1);
                    nextB2 ^= Unsafe.Add(ref toB, 2);
                    nextB3 ^= Unsafe.Add(ref toB, 3);
                }

                ref var newA = ref Unsafe.Subtract(ref a, 4);
                newA = nextA0;
                Unsafe.Add(ref newA, 1) = nextA1;
                Unsafe.Add(ref newA, 2) = nextA2;
                Unsafe.Add(ref newA, 3) = nextA3;

                ref var newB = ref Unsafe.Subtract(ref bRegister, 4);
                newB = nextB0 ^ ((nextB0 ^ nextB3) & p);
                Unsafe.Add(ref newB, 1) = nextB1 ^ ((nextB1 ^ nextB0) & p);
                Unsafe.Add(ref newB, 2) = nextB2 ^ ((nextB2 ^ nextB1) & p);
                Unsafe.Add(ref newB, 3) = nextB3 ^ ((nextB3 ^ nextB2) & p);

                // T3: D = E ^ Z ^ extra B. T4: F = E + Z + r where q is set, else E; E = old F.
                var t0 = z0 ^ e0;
                var t1 = z0 & e0;
                var oldF = f0;
                f0 = e0 ^ (q & (z0 ^ r));
                d0 = extraB0 ^ t0;
                var carry = t1 | (t0 & r);
                e0 = oldF;

                t0 = z1 ^ e1;
                t1 = z1 & e1;
                oldF = f1;
                f1 = e1 ^ (q & (z1 ^ carry));
                d1 = extraB1 ^ t0;
                carry = t1 | (t0 & carry);
                e1 = oldF;

                t0 = z2 ^ e2;
                t1 = z2 & e2;
                oldF = f2;
                f2 = e2 ^ (q & (z2 ^ carry));
                d2 = extraB2 ^ t0;
                carry = t1 | (t0 & carry);
                e2 = oldF;

                t0 = z3 ^ e3;
                t1 = z3 & e3;
                oldF = f3;
                f3 = e3 ^ (q & (z3 ^ carry));
                d3 = extraB3 ^ t0;
                e3 = oldF;
                r ^= q & ((t1 | (t0 & carry)) ^ r);

                // The seven S-boxes, on A as it was before this clock.
                Sbox1(Cell(ref a, 0, 2), Cell(ref a, 5, 1), Cell(ref a, 6, 3), Cell(ref a, 8, 0), Cell(ref a, 3, 0), out x0, out z2);
                Sbox2(Cell(ref a, 2, 2), Cell(ref a, 5, 3), Cell(ref a, 6, 0), Cell(ref a, 8, 1), Cell(ref a, 1, 1), out x1, out z3);
                Sbox3(Cell(ref a, 1, 0), Cell(ref a, 4, 1), Cell(ref a, 4, 3), Cell(ref a, 5, 2), Cell(ref a, 0, 3), out y0, out x2);
                Sbox4(Cell(ref a, 0, 1), Cell(ref a, 1, 3), Cell(ref a, 3, 2), Cell(ref a, 7, 0), Cell(ref a, 2, 3), out y1, out x3);
                Sbox5(Cell(ref a, 3, 3), Cell(ref a, 5, 0), Cell(ref a, 7, 1), Cell(ref a, 8, 2), Cell(ref a, 4, 2), out z0, out y2);
                Sbox6(Cell(ref a, 3, 1), Cell(ref a, 4, 0), Cell(ref a, 6, 2), Cell(ref a, 8, 3), Cell(ref a, 2, 1), out z1, out y3);
                Sbox7(Cell(ref a, 2, 0), Cell(ref a, 6, 1), Cell(ref a, 7, 2), Cell(ref a, 7, 3), Cell(ref a, 1, 2), out p, out q);

                a = ref Unsafe.Subtract(ref a, 4);
                bRegister = ref Unsafe.Subtract(ref bRegister, 4);

                if (!initialising)
                {
                    // Two keystream bits per clock, the most significant pair of the byte first.
                    Unsafe.Add(ref sb, (i * 8) + 7 - (2 * j)) = d2 ^ d3;
                    Unsafe.Add(ref sb, (i * 8) + 6 - (2 * j)) = d0 ^ d1;
                }
            }
        }

        // The window is used up: its last ten cells go back to the end for the next call.
        _a.AsSpan(0, 40).CopyTo(_a.AsSpan(WindowClocks * 4));
        _b.AsSpan(0, 40).CopyTo(_b.AsSpan(WindowClocks * 4));

        _state = new State
        {
            X0 = x0, X1 = x1, X2 = x2, X3 = x3,
            Y0 = y0, Y1 = y1, Y2 = y2, Y3 = y3,
            Z0 = z0, Z1 = z1, Z2 = z2, Z3 = z3,
            D0 = d0, D1 = d1, D2 = d2, D3 = d3,
            E0 = e0, E1 = e1, E2 = e2, E3 = e3,
            F0 = f0, F1 = f1, F2 = f2, F3 = f3,
            P = p, Q = q, R = r,
        };
    }

    /// <summary>libdvbcsa's <c>A[cell][bit]</c>: cell 0 is the newest.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> Cell(ref Vector128<ulong> register, int cell, int bit) =>
        Unsafe.Add(ref register, (cell * 4) + bit);

    // The seven S-boxes as boolean formulas, libdvbcsa's dvbcsa_bs_stream_sbox1 to 7.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Sbox1(Vector128<ulong> fa, Vector128<ulong> fb, Vector128<ulong> fc, Vector128<ulong> fd, Vector128<ulong> fe, out Vector128<ulong> sa, out Vector128<ulong> sb)
    {
        var tmp0 = fa ^ (fb ^ ~(((fa | fb) ^ fc) | (fc ^ fd)));
        var tmp1 = (fa | fb) ^ ~(fc & (fa | (fb ^ fd)));
        var tmp2 = fa ^ ((fb & fd) ^ ((fa & fd) | fc));
        var tmp3 = (fa & fc) ^ (fa ^ ((fa & fb) | fd));
        sa = tmp0 ^ (fe & tmp1);
        sb = tmp2 ^ (fe & tmp3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Sbox2(Vector128<ulong> fa, Vector128<ulong> fb, Vector128<ulong> fc, Vector128<ulong> fd, Vector128<ulong> fe, out Vector128<ulong> sa, out Vector128<ulong> sb)
    {
        var tmp0 = fa ^ ((fb & (fc | fd)) ^ (fc ^ ~fd));
        var tmp1 = (fa & (fb ^ fd)) | ((fa | fb) & fc);
        var tmp2 = (fb & fd) ^ ((fa & fd) | (fb ^ ~fc));
        var tmp3 = (fa & fd) | (fa ^ (fb ^ (fc & fd)));
        sa = tmp0 ^ (fe & tmp1);
        sb = tmp2 ^ (fe & tmp3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Sbox3(Vector128<ulong> fa, Vector128<ulong> fb, Vector128<ulong> fc, Vector128<ulong> fd, Vector128<ulong> fe, out Vector128<ulong> sa, out Vector128<ulong> sb)
    {
        var tmp0 = fa ^ (fb ^ ((fc & (fa | fd)) ^ fd));
        var tmp1 = (fa & fc) ^ ((fa ^ fd) | ((fb | fc) ^ ~fd));
        var tmp2 = fa ^ (((fb ^ fc) & fd) ^ fc);
        sa = tmp0 ^ (~fe & tmp1);
        sb = tmp2 ^ fe;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Sbox4(Vector128<ulong> fa, Vector128<ulong> fb, Vector128<ulong> fc, Vector128<ulong> fd, Vector128<ulong> fe, out Vector128<ulong> sa, out Vector128<ulong> sb)
    {
        var tmp0 = fa ^ ((fc & (fa ^ fd)) | (fb ^ (fc | ~fd)));
        var tmp1 = (fa & fb) ^ (fb ^ (((fa | fc) & fd) ^ fc));
        var tmp2 = fa ^ ((fb & fc) | (((fa & (fb ^ fd)) | fc) ^ fd));
        sa = tmp0 ^ (fe & (tmp1 ^ tmp0));
        sb = sa ^ tmp2 ^ fe;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Sbox5(Vector128<ulong> fa, Vector128<ulong> fb, Vector128<ulong> fc, Vector128<ulong> fd, Vector128<ulong> fe, out Vector128<ulong> sa, out Vector128<ulong> sb)
    {
        var tmp0 = ((fa & (fb | fc)) ^ fb) | ~((fa ^ fc) | fd);
        var tmp1 = fb ^ ((fc ^ fd) & (fc ^ (fb | (fa ^ fd))));
        var tmp2 = (fa & fc) ^ (fb ^ ((fb | (fa ^ fc)) & fd));
        var tmp3 = ((fa ^ fb) & ~fc) | fd;
        sa = tmp0 ^ (fe & tmp1);
        sb = tmp2 ^ (fe & tmp3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Sbox6(Vector128<ulong> fa, Vector128<ulong> fb, Vector128<ulong> fc, Vector128<ulong> fd, Vector128<ulong> fe, out Vector128<ulong> sa, out Vector128<ulong> sb)
    {
        var tmp0 = (fa & fc & fd) ^ ((fb & (fa | fd)) ^ fc);
        var tmp1 = ~((fa ^ fc) & fd);
        var tmp2 = (fa & (fb | fc)) ^ (fb ^ ((fb & fc) | fd));
        var tmp3 = fc & ((fa & (fb ^ fd)) ^ (fb | fd));
        sa = tmp0 ^ (fe & tmp1);
        sb = tmp2 ^ (fe & tmp3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Sbox7(Vector128<ulong> fa, Vector128<ulong> fb, Vector128<ulong> fc, Vector128<ulong> fd, Vector128<ulong> fe, out Vector128<ulong> sa, out Vector128<ulong> sb)
    {
        var tmp0 = fb ^ ((fc & fd) | (fa ^ (fc ^ fd)));
        var tmp1 = (fb | fd) & ((fa & fc) | (fb ^ (fc ^ fd)));
        var tmp2 = (fa | fb) ^ ((fc & (fb | fd)) ^ fd);
        var tmp3 = fd | ~(fa & fc);
        sa = tmp0 ^ (fe & tmp1);
        sb = tmp2 ^ (fe & tmp3);
    }

    /// <summary>
    /// <c>dvbcsa_bs_matrix_transpose_64x</c>: transposes the two 64x64 bit matrices held in the
    /// low and the high halves of the 64 rows. Its own inverse.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Transpose64<TSwaps>(Vector128<ulong>[] row)
        where TSwaps : struct, ICsaSwaps
    {
        ref var r = ref MemoryMarshal.GetArrayDataReference(row);
        _ = row[63];

        for (var j = 0; j < 64; j += 8)
        {
            ref var s = ref Unsafe.Add(ref r, j);
            var a0 = s;
            var a1 = Unsafe.Add(ref s, 1);
            var a2 = Unsafe.Add(ref s, 2);
            var a3 = Unsafe.Add(ref s, 3);
            var b0 = Unsafe.Add(ref s, 4);
            var b1 = Unsafe.Add(ref s, 5);
            var b2 = Unsafe.Add(ref s, 6);
            var b3 = Unsafe.Add(ref s, 7);

            CsaBitMatrix.Swap1(ref a0, ref a1);
            CsaBitMatrix.Swap1(ref a2, ref a3);
            CsaBitMatrix.Swap1(ref b0, ref b1);
            CsaBitMatrix.Swap1(ref b2, ref b3);

            CsaBitMatrix.Swap2(ref a0, ref a2);
            CsaBitMatrix.Swap2(ref a1, ref a3);
            CsaBitMatrix.Swap2(ref b0, ref b2);
            CsaBitMatrix.Swap2(ref b1, ref b3);

            CsaBitMatrix.Swap4(ref a0, ref b0);
            CsaBitMatrix.Swap4(ref a1, ref b1);
            CsaBitMatrix.Swap4(ref a2, ref b2);
            CsaBitMatrix.Swap4(ref a3, ref b3);

            s = a0;
            Unsafe.Add(ref s, 1) = a1;
            Unsafe.Add(ref s, 2) = a2;
            Unsafe.Add(ref s, 3) = a3;
            Unsafe.Add(ref s, 4) = b0;
            Unsafe.Add(ref s, 5) = b1;
            Unsafe.Add(ref s, 6) = b2;
            Unsafe.Add(ref s, 7) = b3;
        }

        for (var j = 0; j < 8; j++)
        {
            ref var s = ref Unsafe.Add(ref r, j);
            var a0 = s;
            var a1 = Unsafe.Add(ref s, 8);
            var a2 = Unsafe.Add(ref s, 16);
            var a3 = Unsafe.Add(ref s, 24);
            var b0 = Unsafe.Add(ref s, 32);
            var b1 = Unsafe.Add(ref s, 40);
            var b2 = Unsafe.Add(ref s, 48);
            var b3 = Unsafe.Add(ref s, 56);

            TSwaps.Swap8(ref a0, ref a1);
            TSwaps.Swap8(ref a2, ref a3);
            TSwaps.Swap8(ref b0, ref b1);
            TSwaps.Swap8(ref b2, ref b3);

            TSwaps.Swap16(ref a0, ref a2);
            TSwaps.Swap16(ref a1, ref a3);
            TSwaps.Swap16(ref b0, ref b2);
            TSwaps.Swap16(ref b1, ref b3);

            TSwaps.Swap32(ref a0, ref b0);
            TSwaps.Swap32(ref a1, ref b1);
            TSwaps.Swap32(ref a2, ref b2);
            TSwaps.Swap32(ref a3, ref b3);

            s = a0;
            Unsafe.Add(ref s, 8) = a1;
            Unsafe.Add(ref s, 16) = a2;
            Unsafe.Add(ref s, 24) = a3;
            Unsafe.Add(ref s, 32) = b0;
            Unsafe.Add(ref s, 40) = b1;
            Unsafe.Add(ref s, 48) = b2;
            Unsafe.Add(ref s, 56) = b3;
        }
    }

    /// <summary>The cipher's state between two calls of <see cref="Clock"/>, register A and B aside.</summary>
    private struct State
    {
        public Vector128<ulong> X0, X1, X2, X3;
        public Vector128<ulong> Y0, Y1, Y2, Y3;
        public Vector128<ulong> Z0, Z1, Z2, Z3;
        public Vector128<ulong> D0, D1, D2, D3;
        public Vector128<ulong> E0, E1, E2, E3;
        public Vector128<ulong> F0, F1, F2, F3;
        public Vector128<ulong> P, Q, R;
    }
}
