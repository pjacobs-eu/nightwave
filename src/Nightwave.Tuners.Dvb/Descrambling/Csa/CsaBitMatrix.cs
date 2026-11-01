// Portions ported from libdvbcsa (src/dvbcsa_bs_transpose.h, src/dvbcsa_bs_neon.h), as
// published in https://github.com/OpenDelta-Team/libdvbcsa at commit
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

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;

namespace Nightwave.Tuners.Dvb.Descrambling.Csa;

/// <summary>Which instructions the batched CSA uses. Every path produces the same bytes.</summary>
internal enum CsaVectorPath
{
    /// <summary>
    /// <see cref="Vector128{T}"/> operators only, which every platform has, and the block cipher's
    /// S-box looked up byte by byte.
    /// </summary>
    Portable,

    /// <summary>
    /// x86 with SSSE3 (Intel Jasper Lake and everything since 2006): the S-box and the bit
    /// permutation looked up together from one 16 bit table, split apart with <c>pshufb</c>.
    /// </summary>
    Ssse3,

    /// <summary>
    /// 64 bit ARM: the whole 256 byte S-box held in 16 registers and looked up 16 bytes at a
    /// time with <c>tbl</c>/<c>tbx</c>, and the transposes done with <c>trn1</c>/<c>trn2</c>.
    /// </summary>
    Arm64,
}

/// <summary>
/// The pieces of a bit matrix transpose: swapping the off-diagonal halves of 2x2 blocks between
/// two 128 bit rows, at a granularity of 1 to 32 bits. Little-endian, like every platform .NET
/// runs this on.
/// </summary>
/// <remarks>
/// A struct per instruction set, so that generic code over <see cref="ICsaSwaps"/> is compiled
/// once per path with the calls inlined, not dispatched per swap.
/// </remarks>
internal interface ICsaSwaps
{
    /// <summary>
    /// For every 2n bit unit: the upper n bits of <paramref name="low"/> and the lower n bits of
    /// <paramref name="high"/> trade places, for n = 8.
    /// </summary>
    static abstract void Swap8(ref Vector128<ulong> low, ref Vector128<ulong> high);

    /// <inheritdoc cref="Swap8"/>
    static abstract void Swap16(ref Vector128<ulong> low, ref Vector128<ulong> high);

    /// <inheritdoc cref="Swap8"/>
    static abstract void Swap32(ref Vector128<ulong> low, ref Vector128<ulong> high);
}

/// <summary>Shifts and masks: SSE2 on x86, and anything else.</summary>
internal readonly struct CsaPortableSwaps : ICsaSwaps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Swap8(ref Vector128<ulong> low, ref Vector128<ulong> high) =>
        CsaBitMatrix.SwapBits(ref low, ref high, 8, Vector128.Create((ushort)0x00ff).AsUInt64());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Swap16(ref Vector128<ulong> low, ref Vector128<ulong> high) =>
        CsaBitMatrix.SwapBits(ref low, ref high, 16, Vector128.Create(0x0000_ffffU).AsUInt64());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Swap32(ref Vector128<ulong> low, ref Vector128<ulong> high) =>
        CsaBitMatrix.SwapBits(ref low, ref high, 32, Vector128.Create(0x0000_0000_ffff_ffffUL));
}

/// <summary>ARM: one <c>trn1</c> and one <c>trn2</c> per swap, as libdvbcsa's NEON build does.</summary>
internal readonly struct CsaArm64Swaps : ICsaSwaps
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Swap8(ref Vector128<ulong> low, ref Vector128<ulong> high)
    {
        var l = low.AsByte();
        var h = high.AsByte();
        low = AdvSimd.Arm64.TransposeEven(l, h).AsUInt64();
        high = AdvSimd.Arm64.TransposeOdd(l, h).AsUInt64();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Swap16(ref Vector128<ulong> low, ref Vector128<ulong> high)
    {
        var l = low.AsUInt16();
        var h = high.AsUInt16();
        low = AdvSimd.Arm64.TransposeEven(l, h).AsUInt64();
        high = AdvSimd.Arm64.TransposeOdd(l, h).AsUInt64();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Swap32(ref Vector128<ulong> low, ref Vector128<ulong> high)
    {
        var l = low.AsUInt32();
        var h = high.AsUInt32();
        low = AdvSimd.Arm64.TransposeEven(l, h).AsUInt64();
        high = AdvSimd.Arm64.TransposeOdd(l, h).AsUInt64();
    }
}

internal static class CsaBitMatrix
{
    /// <summary>
    /// <c>BS_SWAP_BITS_LE</c>: the bits of <paramref name="low"/> above <paramref name="mask"/>,
    /// shifted down by <paramref name="shift"/>, trade places with the bits of
    /// <paramref name="high"/> under it. 64 bit shifts serve every granularity: what crosses a
    /// unit boundary is masked away.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SwapBits(ref Vector128<ulong> low, ref Vector128<ulong> high, [ConstantExpected] byte shift, Vector128<ulong> mask)
    {
        var t = ((low >>> shift) ^ high) & mask;
        high ^= t;
        low ^= t << shift;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Swap1(ref Vector128<ulong> low, ref Vector128<ulong> high) =>
        SwapBits(ref low, ref high, 1, Vector128.Create((byte)0x55).AsUInt64());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Swap2(ref Vector128<ulong> low, ref Vector128<ulong> high) =>
        SwapBits(ref low, ref high, 2, Vector128.Create((byte)0x33).AsUInt64());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Swap4(ref Vector128<ulong> low, ref Vector128<ulong> high) =>
        SwapBits(ref low, ref high, 4, Vector128.Create((byte)0x0f).AsUInt64());

    public static CsaVectorPath BestPath =>
        AdvSimd.Arm64.IsSupported ? CsaVectorPath.Arm64
        : System.Runtime.Intrinsics.X86.Ssse3.IsSupported ? CsaVectorPath.Ssse3
        : CsaVectorPath.Portable;

    public static bool IsSupported(CsaVectorPath path) => path switch
    {
        CsaVectorPath.Portable => true,
        CsaVectorPath.Ssse3 => System.Runtime.Intrinsics.X86.Ssse3.IsSupported,
        CsaVectorPath.Arm64 => AdvSimd.Arm64.IsSupported,
        _ => false,
    };
}
