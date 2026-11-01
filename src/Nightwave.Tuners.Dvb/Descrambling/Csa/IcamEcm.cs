namespace Nightwave.Tuners.Dvb.Descrambling.Csa;

/// <summary>
/// Reads from an ECM whether the control word it carries is meant for the iCAM key schedule,
/// see <see cref="CsaKey"/>.
/// </summary>
/// <remarks>
/// <para>
/// Only VideoGuard (CAID 0x0900 to 0x09FF) uses it. Such an ECM is an iCAM one when byte 4 of
/// the section is not zero and byte 2 (the low byte of the section length) is exactly 4 more
/// than byte 4. Its mode byte is then byte 0x15 of the section; any other ECM has mode 0.
/// </para>
/// <para>
/// The open implementations agree on the detection and differ slightly in where they take the
/// mode from: minisatip (src/dvbapi.cpp) takes byte 0x15 as here; the reference implementation's
/// stream relay (module-streamrelay.c, <c>get_ecm_mode</c>) the low nibble of the last ECM byte;
/// and the descrambling server itself makes the same test and then hands over the control word
/// as CSA-ALT, the one alternative schedule there is. The first two hand the value to
/// <c>dvbcsa_bs_key_set_ecm()</c>, which changes the schedule for mode 4 only. Mode 4 is
/// therefore the one that is implemented; any other non-zero mode is reported (it would mean a
/// key schedule nobody has published) and descrambled as plain CSA, which is what the patched
/// libdvbcsa does as well.
/// </para>
/// <para>
/// Which key a mode belongs to: an ECM with table id 0x80 carries the control word for the even
/// parity, 0x81 for the odd one (the "standard" ECM parity, which the reference implementation's
/// module-dvbapi.c filters by). The control word the server then returns for a parity is scheduled
/// with the mode of the last ECM of that parity. Where no ECM of that parity has been seen yet, the
/// last ECM of either parity decides; in practice a service is iCAM throughout or not at all.
/// </para>
/// </remarks>
internal static class IcamEcm
{
    /// <summary>Where the mode byte sits in an iCAM ECM section.</summary>
    public const int ModeOffset = 0x15;

    public static bool IsVideoGuard(int caid) => (caid >> 8) == 0x09;

    /// <summary>The key schedule mode for control words from this ECM section, 0 for plain CSA.</summary>
    public static byte ModeOf(ReadOnlySpan<byte> section, int caid)
    {
        if (!IsVideoGuard(caid) || section.Length <= ModeOffset)
        {
            return 0;
        }

        return section[4] != 0 && section[2] - section[4] == 4 ? section[ModeOffset] : (byte)0;
    }

    /// <summary>0 for even, 1 for odd, -1 when the table id is not an ECM's.</summary>
    public static int ParityOf(ReadOnlySpan<byte> section) =>
        section.Length > 0 && (section[0] & 0xfe) == 0x80 ? section[0] & 1 : -1;
}
