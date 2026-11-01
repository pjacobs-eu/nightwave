namespace Nightwave.Core.Analysis;

/// <summary>Reads bits, and the Exp-Golomb codes H.264 and HEVC write their headers in.</summary>
internal ref struct BitReader(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    public readonly bool IsPastEnd => _position > _data.Length * 8;

    public int Bits(int count)
    {
        var value = 0;

        for (var index = 0; index < count; index++)
        {
            value = (value << 1) | Bit();
        }

        return value;
    }

    public long LongBits(int count)
    {
        long value = 0;

        for (var index = 0; index < count; index++)
        {
            value = (value << 1) | (long)Bit();
        }

        return value;
    }

    public void Skip(int count) => _position += count;

    public bool Flag() => Bit() == 1;

    public int Bit()
    {
        var at = _position++;

        return at / 8 < _data.Length ? (_data[at / 8] >> (7 - (at % 8))) & 1 : 0;
    }

    /// <summary>Unsigned Exp-Golomb. A header cut short reads as zero rather than running on.</summary>
    public int Golomb()
    {
        var zeros = 0;

        while (Bit() == 0)
        {
            if (++zeros > 31 || IsPastEnd)
            {
                return 0;
            }
        }

        return zeros == 0 ? 0 : (1 << zeros) - 1 + Bits(zeros);
    }

    public int SignedGolomb()
    {
        var code = Golomb();

        return (code & 1) == 1 ? (code + 1) / 2 : -(code / 2);
    }
}
