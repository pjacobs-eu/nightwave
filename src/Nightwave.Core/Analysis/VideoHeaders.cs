namespace Nightwave.Core.Analysis;

/// <summary>What a video stream's own headers say. Read from the headers; no picture is decoded.</summary>
public sealed record VideoFormat
{
    public required string Codec { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Frames per second, where the stream says.</summary>
    public double? FrameRate { get; init; }

    public bool Interlaced { get; init; }

    public string? Profile { get; init; }

    /// <summary>For example 4.0 or 5.1.</summary>
    public string? Level { get; init; }

    public int? BitDepth { get; init; }

    public string? ChromaFormat { get; init; }

    /// <summary><c>PQ</c> or <c>HLG</c> where the stream declares one; null is standard dynamic range or unknown.</summary>
    public string? Hdr { get; init; }

    public string? AspectRatio { get; init; }
}

/// <summary>Finds and reads the sequence headers of MPEG-2, H.264 and HEVC in the start of a PES payload.</summary>
public static class VideoHeaders
{
    private static readonly string[] Chroma = ["4:0:0", "4:2:0", "4:2:2", "4:4:4"];

    public static VideoFormat? Find(ReadOnlySpan<byte> payload, string codec)
    {
        for (var index = 0; index + 4 < payload.Length; index++)
        {
            if (payload[index] != 0 || payload[index + 1] != 0 || payload[index + 2] != 1)
            {
                continue;
            }

            var start = index + 3;
            var header = payload[start];
            var rest = payload[(start + 1)..];

            switch (codec)
            {
                case "H.264" when (header & 0x1F) == 7:
                    return H264(Unescape(rest));

                case "HEVC" when ((header >> 1) & 0x3F) == 33:
                    return rest.Length < 2 ? null : Hevc(Unescape(rest[1..]));

                case "MPEG-2" when header == 0xB3:
                    return Mpeg2(rest);
            }
        }

        return null;
    }

    /// <summary>Takes out the emulation prevention bytes, up to the next start code.</summary>
    private static byte[] Unescape(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(Math.Min(data.Length, 256));

        for (var index = 0; index < data.Length && output.Count < 512; index++)
        {
            if (index + 2 < data.Length && data[index] == 0 && data[index + 1] == 0)
            {
                if (data[index + 2] == 3)
                {
                    output.Add(0);
                    output.Add(0);
                    index += 2;

                    continue;
                }

                if (data[index + 2] <= 1)
                {
                    break;
                }
            }

            output.Add(data[index]);
        }

        return [.. output];
    }

    private static VideoFormat? H264(ReadOnlySpan<byte> sps)
    {
        if (sps.Length < 4)
        {
            return null;
        }

        var bits = new BitReader(sps);
        var profile = bits.Bits(8);

        bits.Skip(8);

        var level = bits.Bits(8);

        bits.Golomb();

        var chroma = 1;
        var depth = 8;

        if (profile is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
        {
            chroma = bits.Golomb();

            if (chroma == 3)
            {
                bits.Skip(1);
            }

            depth = bits.Golomb() + 8;
            bits.Golomb();
            bits.Skip(1);

            if (bits.Flag())
            {
                for (var list = 0; list < (chroma == 3 ? 12 : 8); list++)
                {
                    if (bits.Flag())
                    {
                        SkipScalingList(ref bits, list < 6 ? 16 : 64);
                    }
                }
            }
        }

        bits.Golomb();

        var pocType = bits.Golomb();

        if (pocType == 0)
        {
            bits.Golomb();
        }
        else if (pocType == 1)
        {
            bits.Skip(1);
            bits.SignedGolomb();
            bits.SignedGolomb();

            var cycle = bits.Golomb();

            for (var index = 0; index < cycle && index < 256; index++)
            {
                bits.SignedGolomb();
            }
        }

        bits.Golomb();
        bits.Skip(1);

        var widthUnits = bits.Golomb() + 1;
        var heightUnits = bits.Golomb() + 1;
        var progressive = bits.Flag();

        if (!progressive)
        {
            bits.Skip(1);
        }

        bits.Skip(1);

        int cropLeft = 0, cropRight = 0, cropTop = 0, cropBottom = 0;

        if (bits.Flag())
        {
            cropLeft = bits.Golomb();
            cropRight = bits.Golomb();
            cropTop = bits.Golomb();
            cropBottom = bits.Golomb();
        }

        var field = progressive ? 1 : 2;
        var cropX = chroma is 1 or 2 ? 2 : 1;
        var cropY = (chroma == 1 ? 2 : 1) * field;
        double? frameRate = null;
        string? hdr = null;

        if (bits.Flag())
        {
            (frameRate, hdr) = H264Vui(ref bits);
        }

        if (bits.IsPastEnd)
        {
            return null;
        }

        return new VideoFormat
        {
            Codec = "H.264",
            Width = (widthUnits * 16) - (cropX * (cropLeft + cropRight)),
            Height = (field * heightUnits * 16) - (cropY * (cropTop + cropBottom)),
            FrameRate = frameRate,
            Interlaced = !progressive,
            Profile = profile switch
            {
                66 => "Baseline",
                77 => "Main",
                88 => "Extended",
                100 => "High",
                110 => "High 10",
                122 => "High 4:2:2",
                244 => "High 4:4:4",
                _ => profile.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            Level = (level / 10d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            BitDepth = depth,
            ChromaFormat = chroma is >= 0 and < 4 ? Chroma[chroma] : null,
            Hdr = hdr,
        };
    }

    private static void SkipScalingList(ref BitReader bits, int size)
    {
        int last = 8, next = 8;

        for (var index = 0; index < size; index++)
        {
            if (next != 0)
            {
                next = (last + bits.SignedGolomb() + 256) % 256;
            }

            last = next == 0 ? last : next;
        }
    }

    private static (double? FrameRate, string? Hdr) H264Vui(ref BitReader bits)
    {
        string? hdr = null;

        if (bits.Flag() && bits.Bits(8) == 255)
        {
            bits.Skip(32);
        }

        if (bits.Flag())
        {
            bits.Skip(1);
        }

        if (bits.Flag())
        {
            bits.Skip(4);

            if (bits.Flag())
            {
                bits.Skip(8);
                hdr = TransferName(bits.Bits(8));
                bits.Skip(8);
            }
        }

        if (bits.Flag())
        {
            bits.Golomb();
            bits.Golomb();
        }

        if (!bits.Flag())
        {
            return (null, hdr);
        }

        var tick = bits.LongBits(32);
        var scale = bits.LongBits(32);

        return (tick > 0 && scale > 0 ? scale / (2d * tick) : null, hdr);
    }

    private static string? TransferName(int transfer) => transfer switch
    {
        16 => "PQ",
        18 => "HLG",
        _ => null,
    };

    private static VideoFormat? Hevc(ReadOnlySpan<byte> sps)
    {
        if (sps.Length < 16)
        {
            return null;
        }

        var bits = new BitReader(sps);

        bits.Skip(4);

        var subLayers = bits.Bits(3);

        bits.Skip(1);

        bits.Skip(3);

        var profile = bits.Bits(5);

        bits.Skip(32);
        bits.Skip(1);

        var interlaced = bits.Flag();

        bits.Skip(46);

        var level = bits.Bits(8);
        var presence = new (bool Profile, bool Level)[subLayers];

        for (var index = 0; index < subLayers; index++)
        {
            presence[index] = (bits.Flag(), bits.Flag());
        }

        if (subLayers > 0)
        {
            bits.Skip(2 * (8 - subLayers));
        }

        foreach (var (hasProfile, hasLevel) in presence)
        {
            bits.Skip((hasProfile ? 88 : 0) + (hasLevel ? 8 : 0));
        }

        bits.Golomb();

        var chroma = bits.Golomb();

        if (chroma == 3)
        {
            bits.Skip(1);
        }

        var width = bits.Golomb();
        var height = bits.Golomb();

        if (bits.Flag())
        {
            var unitX = chroma is 1 or 2 ? 2 : 1;
            var unitY = chroma == 1 ? 2 : 1;

            width -= unitX * (bits.Golomb() + bits.Golomb());
            height -= unitY * (bits.Golomb() + bits.Golomb());
        }

        var depth = bits.Golomb() + 8;

        if (bits.IsPastEnd || width <= 0 || height <= 0)
        {
            return null;
        }

        return new VideoFormat
        {
            Codec = "HEVC",
            Width = width,
            Height = height,
            Interlaced = interlaced,
            Profile = profile switch
            {
                1 => "Main",
                2 => "Main 10",
                3 => "Main Still Picture",
                4 => "Range Extensions",
                _ => profile.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
            Level = (level / 30d).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            BitDepth = depth,
            ChromaFormat = chroma is >= 0 and < 4 ? Chroma[chroma] : null,
        };
    }

    private static VideoFormat? Mpeg2(ReadOnlySpan<byte> header)
    {
        if (header.Length < 8)
        {
            return null;
        }

        var bits = new BitReader(header);
        var width = bits.Bits(12);
        var height = bits.Bits(12);
        var aspect = bits.Bits(4);
        var rate = bits.Bits(4);

        double[] rates = [0, 24000d / 1001, 24, 25, 30000d / 1001, 30, 50, 60000d / 1001, 60];

        // Whether it is interlaced is in the sequence extension right behind this header.
        var interlaced = false;

        for (var index = 8; index + 5 < header.Length && index < 200; index++)
        {
            if (header[index] == 0 && header[index + 1] == 0 && header[index + 2] == 1 && header[index + 3] == 0xB5 && (header[index + 4] >> 4) == 1)
            {
                interlaced = (header[index + 5] & 0x08) == 0;

                break;
            }
        }

        return new VideoFormat
        {
            Codec = "MPEG-2",
            Width = width,
            Height = height,
            FrameRate = rate is > 0 and < 9 ? rates[rate] : null,
            Interlaced = interlaced,
            BitDepth = 8,
            ChromaFormat = "4:2:0",
            AspectRatio = aspect switch
            {
                1 => "1:1",
                2 => "4:3",
                3 => "16:9",
                4 => "2.21:1",
                _ => null,
            },
        };
    }
}
