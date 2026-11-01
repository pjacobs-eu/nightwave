using System.Security.Cryptography;
using Nightwave.Tuners.Dvb.Descrambling.Csa;

namespace Nightwave.Tuners.Dvb.Descrambling;

/// <summary>
/// The key for one parity of one descrambler index, with the algorithm it belongs to.
/// Immutable, so the socket thread can replace it while the stream thread uses the old one.
/// </summary>
internal abstract class ContentKey
{
    /// <summary>For the log and the session's diagnostics, e.g. "CSA" or "AES-128-CBC".</summary>
    public abstract string Algorithm { get; }

    /// <summary>Descrambles the payloads in place. Called from the stream thread only.</summary>
    public abstract void Descramble(CsaBatchDescrambler csa, Span<byte> buffer, ReadOnlySpan<CsaPayload> payloads);
}

internal sealed class CsaContentKey(CsaKey key) : ContentKey
{
    public CsaKey Key { get; } = key;

    public override string Algorithm => Key.ToString();

    public override void Descramble(CsaBatchDescrambler csa, Span<byte> buffer, ReadOnlySpan<CsaPayload> payloads) =>
        csa.Descramble(Key, buffer, payloads);
}

/// <summary>
/// AES-128 on whole 16 byte blocks, the residue left in the clear. ECB, or CBC restarted with the
/// same initialisation vector for every packet, which is DVB-CISSA (ETSI TS 103 127) when the IV
/// is "DVBTMCPTAESCISSA".
/// </summary>
internal sealed class AesContentKey : ContentKey
{
    /// <summary>The CISSA initialisation vector, used when the server does not send one.</summary>
    public static ReadOnlySpan<byte> CissaIv => "DVBTMCPTAESCISSA"u8;

    private readonly byte[] _key;
    private readonly byte[]? _iv;

    public AesContentKey(ReadOnlySpan<byte> key, ReadOnlySpan<byte> ivOrEmptyForEcb)
    {
        if (key.Length != 16)
        {
            throw new ArgumentException($"An AES-128 key is 16 bytes long, this one is {key.Length}.", nameof(key));
        }

        _key = key.ToArray();
        _iv = ivOrEmptyForEcb.IsEmpty ? null : ivOrEmptyForEcb.ToArray();
        if (_iv is { Length: not 16 })
        {
            throw new ArgumentException($"An AES initialisation vector is 16 bytes long, this one is {_iv.Length}.", nameof(ivOrEmptyForEcb));
        }
    }

    public override string Algorithm => _iv is null ? "AES-128-ECB" : "AES-128-CBC";

    public override void Descramble(CsaBatchDescrambler csa, Span<byte> buffer, ReadOnlySpan<CsaPayload> payloads)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        Span<byte> scratch = stackalloc byte[176];
        foreach (var payload in payloads)
        {
            var whole = payload.Length / 16 * 16;
            if (whole == 0)
            {
                continue;
            }

            var data = buffer.Slice(payload.Offset, whole);
            data.CopyTo(scratch);
            if (_iv is null)
            {
                aes.DecryptEcb(scratch[..whole], data, PaddingMode.None);
            }
            else
            {
                aes.DecryptCbc(scratch[..whole], _iv, data, PaddingMode.None);
            }
        }
    }
}

/// <summary>DES in ECB mode on whole 8 byte blocks, the residue left in the clear.</summary>
internal sealed class DesContentKey : ContentKey
{
    private readonly byte[] _key;

    public DesContentKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != 8)
        {
            throw new ArgumentException($"A DES key is 8 bytes long, this one is {key.Length}.", nameof(key));
        }

        _key = key.ToArray();
    }

    public override string Algorithm => "DES-ECB";

    public override void Descramble(CsaBatchDescrambler csa, Span<byte> buffer, ReadOnlySpan<CsaPayload> payloads)
    {
        // DES is what the broadcaster scrambles with; there is no choosing a stronger cipher here.
#pragma warning disable CA5351
        using var des = DES.Create();
#pragma warning restore CA5351

        // .NET refuses what DES calls weak keys. A broadcaster would not pick one, and if one
        // arrives the caller reports the exception with the service's name.
        des.Key = _key;
        Span<byte> scratch = stackalloc byte[184];
        foreach (var payload in payloads)
        {
            var whole = payload.Length / 8 * 8;
            if (whole == 0)
            {
                continue;
            }

            var data = buffer.Slice(payload.Offset, whole);
            data.CopyTo(scratch);
            des.DecryptEcb(scratch[..whole], data, PaddingMode.None);
        }
    }
}
