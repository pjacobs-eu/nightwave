namespace Nightwave.Tuners.Dvb.Descrambling.Csa;

/// <summary>
/// The constants of the DVB Common Scrambling Algorithm (CSA1, ETSI ETR 289): the block
/// cipher's S-box and bit permutations, the key schedule's bit permutation and the stream
/// cipher's seven small S-boxes with the register bits that feed them.
/// </summary>
/// <remarks>
/// <para>
/// The structure follows the published descriptions of the cipher: R.-P. Weinmann and K. Wirt,
/// "Analysis of the DVB Common Scrambling Algorithm" (CMS 2004), K. Wirt, "Fault attack on the
/// DVB Common Scrambling Algorithm" (ICCSA 2005, IACR ePrint 2004/289), and E. Tews, J. Wälde,
/// M. Weiner, "Breaking DVB-CSA" (WEWoRC 2011). The permutations are written as "bit i goes to
/// bit j" and the stream S-boxes as input bits plus a 32 entry table, which is how those papers
/// present them; the values were checked bit for bit against the widely used implementations
/// with the known-answer vectors in the tests.
/// </para>
/// <para>
/// Register naming follows the papers: the stream cipher has two shift registers A and B of ten
/// 4 bit cells each, A1 being the cell that was shifted in last and A10 the one that falls out
/// next.
/// </para>
/// </remarks>
internal static class CsaTables
{
    /// <summary>Rounds of the block cipher per 8 byte block, and bytes of the expanded key.</summary>
    public const int BlockRounds = 56;

    /// <summary>The block cipher's 8 bit S-box.</summary>
    public static ReadOnlySpan<byte> BlockSbox =>
    [
        0x3a, 0xea, 0x68, 0xfe, 0x33, 0xe9, 0x88, 0x1a, 0x83, 0xcf, 0xe1, 0x7f, 0xba, 0xe2, 0x38, 0x12,
        0xe8, 0x27, 0x61, 0x95, 0x0c, 0x36, 0xe5, 0x70, 0xa2, 0x06, 0x82, 0x7c, 0x17, 0xa3, 0x26, 0x49,
        0xbe, 0x7a, 0x6d, 0x47, 0xc1, 0x51, 0x8f, 0xf3, 0xcc, 0x5b, 0x67, 0xbd, 0xcd, 0x18, 0x08, 0xc9,
        0xff, 0x69, 0xef, 0x03, 0x4e, 0x48, 0x4a, 0x84, 0x3f, 0xb4, 0x10, 0x04, 0xdc, 0xf5, 0x5c, 0xc6,
        0x16, 0xab, 0xac, 0x4c, 0xf1, 0x6a, 0x2f, 0x3c, 0x3b, 0xd4, 0xd5, 0x94, 0xd0, 0xc4, 0x63, 0x62,
        0x71, 0xa1, 0xf9, 0x4f, 0x2e, 0xaa, 0xc5, 0x56, 0xe3, 0x39, 0x93, 0xce, 0x65, 0x64, 0xe4, 0x58,
        0x6c, 0x19, 0x42, 0x79, 0xdd, 0xee, 0x96, 0xf6, 0x8a, 0xec, 0x1e, 0x85, 0x53, 0x45, 0xde, 0xbb,
        0x7e, 0x0a, 0x9a, 0x13, 0x2a, 0x9d, 0xc2, 0x5e, 0x5a, 0x1f, 0x32, 0x35, 0x9c, 0xa8, 0x73, 0x30,
        0x29, 0x3d, 0xe7, 0x92, 0x87, 0x1b, 0x2b, 0x4b, 0xa5, 0x57, 0x97, 0x40, 0x15, 0xe6, 0xbc, 0x0e,
        0xeb, 0xc3, 0x34, 0x2d, 0xb8, 0x44, 0x25, 0xa4, 0x1c, 0xc7, 0x23, 0xed, 0x90, 0x6e, 0x50, 0x00,
        0x99, 0x9e, 0x4d, 0xd9, 0xda, 0x8d, 0x6f, 0x5f, 0x3e, 0xd7, 0x21, 0x74, 0x86, 0xdf, 0x6b, 0x05,
        0x8e, 0x5d, 0x37, 0x11, 0xd2, 0x28, 0x75, 0xd6, 0xa7, 0x77, 0x24, 0xbf, 0xf0, 0xb0, 0x02, 0xb7,
        0xf8, 0xfc, 0x81, 0x09, 0xb1, 0x01, 0x76, 0x91, 0x7d, 0x0f, 0xc8, 0xa0, 0xf2, 0xcb, 0x78, 0x60,
        0xd1, 0xf7, 0xe0, 0xb5, 0x98, 0x22, 0xb3, 0x20, 0x1d, 0xa6, 0xdb, 0x7b, 0x59, 0x9f, 0xae, 0x31,
        0xfb, 0xd3, 0xb6, 0xca, 0x43, 0x72, 0x07, 0xf4, 0xd8, 0x41, 0x14, 0x55, 0x0d, 0x54, 0x8b, 0xb9,
        0xad, 0x46, 0x0b, 0xaf, 0x80, 0x52, 0x2c, 0xfa, 0x8c, 0x89, 0x66, 0xfd, 0xb2, 0xa9, 0x9b, 0xc0,
    ];

    /// <summary>
    /// The block cipher's bit permutation, applied to the S-box output before it is mixed into
    /// the state: bit <c>i</c> of the input becomes bit <c>BlockPermutationTarget[i]</c>.
    /// </summary>
    private static ReadOnlySpan<byte> BlockPermutationTarget => [1, 7, 5, 4, 2, 6, 0, 3];

    /// <summary>
    /// The key schedule's permutation of the 64 key bits (the key read as a little-endian
    /// integer): bit <c>i</c> of one round key group becomes bit <c>KeyPermutationTarget[i]</c>
    /// of the next.
    /// </summary>
    private static ReadOnlySpan<byte> KeyPermutationTarget =>
    [
        19, 27, 55, 46, 1, 15, 36, 22, 56, 61, 39, 21, 54, 58, 50, 28,
        7, 29, 51, 6, 33, 35, 20, 16, 47, 30, 32, 63, 10, 11, 4, 38,
        62, 26, 40, 18, 12, 52, 37, 53, 23, 59, 41, 17, 31, 0, 25, 43,
        44, 14, 2, 13, 45, 48, 3, 60, 49, 8, 34, 5, 9, 42, 57, 24,
    ];

    /// <summary>
    /// The iCAM variant's bit permutation of control word bytes 0 and 4 before the block key
    /// schedule, see <see cref="CsaKey"/>: bit <c>i</c> becomes bit <c>IcamPermutationTarget[i]</c>.
    /// </summary>
    private static ReadOnlySpan<byte> IcamPermutationTarget => [1, 7, 5, 2, 6, 0, 3, 4];

    /// <summary><see cref="BlockPermutationTarget"/> as a lookup table.</summary>
    public static readonly byte[] BlockPermutation = BytePermutationTable(BlockPermutationTarget);

    /// <summary><see cref="IcamPermutationTarget"/> as a lookup table.</summary>
    public static readonly byte[] IcamPermutation = BytePermutationTable(IcamPermutationTarget);

    public static ulong PermuteKey(ulong key)
    {
        ulong permuted = 0;
        var targets = KeyPermutationTarget;
        for (var bit = 0; bit < 64; bit++)
        {
            permuted |= ((key >> bit) & 1) << targets[bit];
        }

        return permuted;
    }

    /// <summary>
    /// One of the stream cipher's seven S-boxes: five bits of register A in, two bits out.
    /// </summary>
    /// <param name="Inputs">
    /// The five input bits, as register cell (1 to 10) and bit within it. Input <c>j</c> is bit
    /// <c>j</c> of the table index.
    /// </param>
    /// <param name="Table">32 entries of two bits: bit 0 goes to the first output, bit 1 to the second.</param>
    /// <param name="Outputs">The two outputs, as indexes into <see cref="StreamOutput"/>.</param>
    /// <param name="LowTruthTable">Bit 0 of every table entry, entry <c>i</c> at bit <c>i</c>.</param>
    /// <param name="HighTruthTable">Bit 1 of every table entry, entry <c>i</c> at bit <c>i</c>.</param>
    public sealed record StreamSbox(
        (int Cell, int Bit)[] Inputs,
        byte[] Table,
        StreamOutput[] Outputs,
        uint LowTruthTable,
        uint HighTruthTable);

    /// <summary>The 14 bits the S-boxes produce: the nibbles X, Y and Z and the bits p and q.</summary>
    public enum StreamOutput
    {
        X0, X1, X2, X3, Y0, Y1, Y2, Y3, Z0, Z1, Z2, Z3, P, Q,
    }

    /// <summary>
    /// The seven stream S-boxes. The truth tables are the tables again, one output bit at a time,
    /// which is what the batched implementation evaluates; a test holds the two in step.
    /// </summary>
    public static readonly StreamSbox[] StreamSboxes =
    [
        new([(2, 2), (5, 0), (7, 1), (8, 3), (10, 0)],
            [1, 3, 0, 1, 1, 2, 1, 2, 2, 1, 3, 0, 3, 0, 2, 3, 0, 1, 3, 1, 3, 2, 1, 2, 2, 0, 0, 3, 0, 3, 2, 0],
            [StreamOutput.X0, StreamOutput.Z2], 0x285E965B, 0x69B4D5A2),
        new([(3, 1), (4, 2), (7, 3), (8, 0), (10, 1)],
            [3, 3, 2, 0, 1, 3, 0, 1, 0, 0, 1, 2, 3, 0, 2, 3, 2, 2, 3, 0, 3, 1, 0, 2, 1, 3, 2, 1, 0, 1, 1, 2],
            [StreamOutput.X1, StreamOutput.Z3], 0x6B3494B3, 0x8697D827),
        new([(2, 3), (3, 0), (6, 1), (6, 3), (7, 2)],
            [2, 1, 1, 2, 2, 3, 3, 0, 1, 0, 0, 1, 3, 2, 2, 3, 0, 3, 1, 0, 3, 0, 0, 3, 2, 1, 3, 2, 1, 2, 0, 1],
            [StreamOutput.X2, StreamOutput.Y0], 0x96969966, 0x2D92F079),
        new([(2, 1), (3, 3), (4, 3), (5, 2), (9, 0)],
            [3, 1, 0, 3, 1, 0, 2, 1, 2, 0, 1, 0, 3, 2, 0, 2, 1, 2, 2, 0, 0, 3, 3, 2, 3, 1, 2, 3, 1, 0, 3, 1],
            [StreamOutput.X3, StreamOutput.Y1], 0xDB61149B, 0x4DE6B149),
        new([(5, 3), (6, 2), (7, 0), (9, 1), (10, 2)],
            [2, 0, 2, 1, 3, 1, 0, 3, 0, 3, 2, 3, 3, 2, 1, 0, 0, 1, 3, 0, 2, 0, 3, 1, 1, 3, 0, 2, 2, 1, 1, 2],
            [StreamOutput.Y2, StreamOutput.Z0], 0x63C65AB8, 0x9A543E95),
        new([(4, 1), (5, 1), (6, 0), (8, 2), (10, 3)],
            [0, 2, 0, 2, 1, 3, 2, 0, 2, 0, 3, 1, 2, 1, 1, 3, 1, 3, 1, 1, 2, 0, 3, 3, 3, 2, 0, 2, 0, 1, 3, 0],
            [StreamOutput.Y3, StreamOutput.Z1], 0x61CFEC30, 0x4BD2956A),
        new([(3, 2), (4, 0), (8, 1), (9, 2), (9, 3)],
            [0, 2, 3, 1, 3, 0, 2, 1, 1, 3, 2, 2, 0, 2, 1, 0, 3, 0, 0, 3, 0, 2, 1, 3, 1, 3, 3, 0, 2, 1, 2, 1],
            [StreamOutput.P, StreamOutput.Q], 0xA7C9439C, 0x56A92E56),
    ];

    /// <summary>
    /// The four bits taken from register B each clock and mixed into D: output bit <c>i</c> is
    /// the XOR of the listed cells and bits.
    /// </summary>
    public static readonly (int Cell, int Bit)[][] StreamBSelection =
    [
        [(4, 1), (7, 3), (9, 0), (10, 2)],
        [(5, 0), (6, 1), (6, 3), (9, 2)],
        [(4, 3), (5, 2), (7, 0), (9, 1)],
        [(4, 0), (7, 1), (8, 2), (10, 3)],
    ];

    private static byte[] BytePermutationTable(ReadOnlySpan<byte> targets)
    {
        var table = new byte[256];
        for (var value = 0; value < 256; value++)
        {
            var permuted = 0;
            for (var bit = 0; bit < 8; bit++)
            {
                permuted |= ((value >> bit) & 1) << targets[bit];
            }

            table[value] = (byte)permuted;
        }

        return table;
    }
}
