using System.Buffers.Binary;

namespace Nightwave.Tuners.Dvb.Descrambling.Csa;

/// <summary>
/// One DVB-CSA control word, expanded for both halves of the cipher. Immutable: a new control
/// word is a new key, which is what lets the socket thread hand keys to the stream thread by
/// swapping a reference.
/// </summary>
/// <remarks>
/// <para>
/// The block cipher uses 56 key bytes. The control word, read as a little-endian 64 bit
/// integer, is the seventh group of eight; each group before it is the bit permutation
/// <see cref="CsaTables.PermuteKey"/> of the one after it, and byte <c>j</c> of group <c>i</c>
/// is XORed with <c>i</c>.
/// </para>
/// <para>
/// iCAM, the variant Sky Deutschland's VideoGuard (CAID 0x09xx) service uses, changes only that
/// schedule: when the ECM that produced the control word has mode 4 (see <see cref="IcamEcm"/>),
/// bytes 0 and 4 of the control word pass through a fixed bit permutation
/// (<see cref="CsaTables.IcamPermutation"/>) before the schedule is built. The stream cipher
/// still uses the control word as it is. Mode 0, and every other value, is plain CSA. Source:
/// the <c>dvbcsa_bs_key_set_ecm()</c> extension of libdvbcsa that minisatip uses, as published
/// in https://github.com/OpenDelta-Team/libdvbcsa
/// (<c>dvbcsa_load_le64_ecm</c>, <c>csa_block_perm_ecm</c>); the permutation is written here as
/// a bit mapping, which reproduces that table exactly (checked in the tests).
/// </para>
/// </remarks>
internal sealed class CsaKey
{
    /// <summary>The ECM mode that switches the iCAM key schedule on.</summary>
    public const byte IcamMode = 4;

    private readonly byte[] _controlWord;
    private readonly byte[] _blockSchedule;

    private CsaKey(byte[] controlWord, byte ecmMode, byte[] blockSchedule)
    {
        _controlWord = controlWord;
        EcmMode = ecmMode;
        _blockSchedule = blockSchedule;
    }

    /// <summary>The eight byte control word as the conditional access server sent it.</summary>
    public ReadOnlySpan<byte> ControlWord => _controlWord;

    /// <summary>0 for plain CSA, <see cref="IcamMode"/> for the iCAM key schedule.</summary>
    public byte EcmMode { get; }

    /// <summary>The 56 bytes of the block cipher key, round 0 first.</summary>
    public ReadOnlySpan<byte> BlockSchedule => _blockSchedule;

    public static CsaKey Create(ReadOnlySpan<byte> controlWord, byte ecmMode = 0)
    {
        if (controlWord.Length != 8)
        {
            throw new ArgumentException(
                $"A DVB-CSA control word is 8 bytes long, this one is {controlWord.Length}.",
                nameof(controlWord));
        }

        var blockKey = controlWord.ToArray();
        if (ecmMode == IcamMode)
        {
            blockKey[0] = CsaTables.IcamPermutation[blockKey[0]];
            blockKey[4] = CsaTables.IcamPermutation[blockKey[4]];
        }

        var schedule = new byte[CsaTables.BlockRounds];
        var group = BinaryPrimitives.ReadUInt64LittleEndian(blockKey);
        for (var i = 6; i >= 0; i--)
        {
            for (var j = 0; j < 8; j++)
            {
                schedule[(i * 8) + j] = (byte)((group >> (j * 8)) ^ (ulong)i);
            }

            group = CsaTables.PermuteKey(group);
        }

        return new CsaKey(controlWord.ToArray(), ecmMode, schedule);
    }

    public override string ToString() => EcmMode == 0 ? "CSA" : $"CSA (iCAM mode {EcmMode})";
}
