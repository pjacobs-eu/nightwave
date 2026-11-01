namespace Nightwave.Core.Analysis;

/// <summary>What an audio stream's frame header says.</summary>
public sealed record AudioFormat
{
    public int? SampleRate { get; init; }

    /// <summary>Kilobits per second, where the frame header states it.</summary>
    public int? BitRate { get; init; }

    /// <summary><c>2.0</c>, <c>5.1</c> and so on.</summary>
    public string? Channels { get; init; }
}

/// <summary>Reads the frame headers of MPEG audio, AC-3, E-AC-3 and ADTS AAC.</summary>
public static class AudioHeaders
{
    private static readonly int[] Layer2Rates = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
    private static readonly int[] MpegSampleRates = [44100, 48000, 32000];
    private static readonly int[] Ac3Rates = [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640];
    private static readonly int[] Ac3SampleRates = [48000, 44100, 32000];
    private static readonly string[] Ac3Channels = ["2.0", "1.0", "2.0", "3.0", "2.1", "3.1", "2.2", "3.2"];
    private static readonly int[] AacSampleRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    public static AudioFormat? Find(ReadOnlySpan<byte> payload, string codec)
    {
        for (var index = 0; index + 7 < payload.Length && index < 512; index++)
        {
            var frame = payload[index..];
            var format = codec switch
            {
                "MPEG Audio" => Mpeg(frame),
                "AC-3" or "E-AC-3" => Dolby(frame),
                "AAC" => Adts(frame),
                _ => null,
            };

            if (format is not null)
            {
                return format;
            }
        }

        return null;
    }

    private static AudioFormat? Mpeg(ReadOnlySpan<byte> frame)
    {
        // Layer II of MPEG-1 is what DVB broadcasts; anything else is not guessed at.
        if (frame[0] != 0xFF || (frame[1] & 0xFE) != 0xFC)
        {
            return null;
        }

        var rate = frame[2] >> 4;
        var sample = (frame[2] >> 2) & 3;

        if (rate is 0 or 15 || sample == 3)
        {
            return null;
        }

        return new AudioFormat
        {
            SampleRate = MpegSampleRates[sample],
            BitRate = Layer2Rates[rate],
            Channels = (frame[3] >> 6) == 3 ? "1.0" : "2.0",
        };
    }

    private static AudioFormat? Dolby(ReadOnlySpan<byte> frame)
    {
        if (frame[0] != 0x0B || frame[1] != 0x77)
        {
            return null;
        }

        var stream = frame[5] >> 3;

        if (stream > 10)
        {
            // E-AC-3: fscod, then acmod and lfeon.
            var code = frame[4] >> 6;
            var mode = (frame[4] >> 1) & 7;

            return code == 3
                ? new AudioFormat { Channels = Channels(mode, (frame[4] & 1) == 1) }
                : new AudioFormat { SampleRate = Ac3SampleRates[code], Channels = Channels(mode, (frame[4] & 1) == 1) };
        }

        var sample = frame[4] >> 6;
        var size = (frame[4] & 0x3F) >> 1;

        if (sample == 3 || size >= Ac3Rates.Length)
        {
            return null;
        }

        // acmod, then the two-bit fields it brings, then lfeon.
        var acmod = frame[6] >> 5;
        var position = 3;

        if ((acmod & 1) != 0 && acmod != 1)
        {
            position += 2;
        }

        if ((acmod & 4) != 0)
        {
            position += 2;
        }

        if (acmod == 2)
        {
            position += 2;
        }

        var lfe = ((((frame[6] << 8) | frame[7]) >> (15 - position)) & 1) == 1;

        return new AudioFormat { SampleRate = Ac3SampleRates[sample], BitRate = Ac3Rates[size], Channels = Channels(acmod, lfe) };
    }

    private static string Channels(int acmod, bool lfe)
    {
        var name = Ac3Channels[acmod & 7];

        if (!lfe)
        {
            return name;
        }

        var parts = name.Split('.');

        return $"{int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) + int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture)}.1";
    }

    private static AudioFormat? Adts(ReadOnlySpan<byte> frame)
    {
        if (frame[0] != 0xFF || (frame[1] & 0xF6) != 0xF0)
        {
            return null;
        }

        var sample = (frame[2] >> 2) & 0xF;
        var channels = ((frame[2] & 1) << 2) | (frame[3] >> 6);

        if (sample >= AacSampleRates.Length)
        {
            return null;
        }

        return new AudioFormat
        {
            SampleRate = AacSampleRates[sample],
            Channels = channels switch
            {
                1 => "1.0",
                2 => "2.0",
                6 => "5.1",
                7 => "7.1",
                _ => null,
            },
        };
    }
}
