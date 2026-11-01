using System.Text;

namespace Nightwave.Tuners.Dvb.Mpeg.Si;

/// <summary>
/// Decodes the text fields of DVB service information (EN 300 468, Annex A): service and
/// provider names, network names. The first byte picks the character table - or is already
/// text, in which case the table is ISO/IEC 6937, the default nobody outside broadcasting uses.
/// </summary>
internal static class DvbText
{
    /// <summary>
    /// ISO/IEC 6937, 0xA0 to 0xFF, with the diacritic prefixes (0xC1 to 0xCF) left out: those are
    /// not characters but marks for the letter that follows. Positions the standard leaves
    /// empty are U+FFFD.
    /// </summary>
    private const string Iso6937Upper =
        " ¡¢£�¥�§¤‘“«←↑→↓" +
        "°±²³×µ¶·÷’”»¼½¾¿" +
        "����������������" +
        "―¹®©™♪¬¦����⅛⅜⅝⅞" +
        "ΩÆĐªĦ�ĲĿŁØŒºÞŦŊŉ" +
        "ĸæđðħıĳŀłøœßþŧŋ­";

    public static string Decode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        var selector = data[0];
        string text;

        if (selector >= 0x20)
        {
            text = DecodeIso6937(data);
        }
        else if (selector is >= 0x01 and <= 0x0B)
        {
            // 0x01 is ISO 8859-5, and so on up to 0x0B for 8859-15; 8859-12 was never finished.
            text = DecodeIso8859(selector + 4, data[1..]);
        }
        else if (selector == 0x10 && data.Length >= 3)
        {
            text = DecodeIso8859((data[1] << 8) | data[2], data[3..]);
        }
        else if (selector == 0x11)
        {
            text = Encoding.BigEndianUnicode.GetString(data[1..]);
        }
        else if (selector == 0x15)
        {
            text = Encoding.UTF8.GetString(data[1..]);
        }
        else if (selector is 0x12 or 0x13 or 0x14)
        {
            // Korean (KS X 1001), simplified Chinese (GB 2312) and traditional Chinese (Big5).
            var codePage = selector switch { 0x12 => 949, 0x13 => 936, _ => 950 };
            text = CodePage(codePage)?.GetString(data[1..]) ?? Encoding.Latin1.GetString(data[1..]);
        }
        else if (selector == 0x1F && data.Length >= 2)
        {
            // An encoding described elsewhere (EN 300 468, Annex A.2): not something a name can
            // be read in without the table it points to.
            text = Encoding.Latin1.GetString(data[2..]);
        }
        else
        {
            text = Encoding.Latin1.GetString(data[1..]);
        }

        return Clean(text);
    }

    /// <summary>
    /// Takes out the control codes (EN 300 468, Annex A.1): emphasis on and off, and line breaks,
    /// which in a name are a space. The two-byte tables put the same codes at U+E080 and up.
    /// </summary>
    private static string Clean(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var character in text)
        {
            switch (character)
            {
                case '\u008A' or '' or '\n' or '\r':
                    builder.Append(' ');
                    break;
                case >= '\u0080' and <= '\u009F':
                case >= '' and <= '':
                case < ' ':
                    break;
                default:
                    builder.Append(character);
                    break;
            }
        }

        return builder.ToString().Trim();
    }

    private static string DecodeIso8859(int part, ReadOnlySpan<byte> data)
    {
        if (part == 1)
        {
            return Encoding.Latin1.GetString(data);
        }

        // The ISO 8859 parts the Windows code page tables carry are 28591 plus the part number.
        var encoding = part is >= 2 and <= 16 ? CodePage(28590 + part) : null;

        return encoding?.GetString(data) ?? Encoding.Latin1.GetString(data);
    }

    private static Encoding? CodePage(int codePage)
    {
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.GetEncoding(codePage);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// ISO/IEC 6937: ASCII below 0x80, the table above for most of the rest, and a diacritic
    /// written before its letter - which Unicode puts after it and then composes.
    /// </summary>
    private static string DecodeIso6937(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder(data.Length);

        for (var index = 0; index < data.Length; index++)
        {
            var value = data[index];

            if (value < 0x80)
            {
                builder.Append((char)value);
            }
            else if (value < 0xA0)
            {
                // Control codes; Clean decides what they become.
                builder.Append((char)value);
            }
            else if (value is >= 0xC1 and <= 0xCF)
            {
                var mark = CombiningMark(value);

                if (index + 1 < data.Length && data[index + 1] is >= 0x20 and < 0x80 && mark is not null)
                {
                    builder.Append((char)data[index + 1]).Append(mark.Value);
                    index++;
                }
            }
            else
            {
                builder.Append(Iso6937Upper[value - 0xA0]);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static char? CombiningMark(byte value) => value switch
    {
        0xC1 => '̀',
        0xC2 => '́',
        0xC3 => '̂',
        0xC4 => '̃',
        0xC5 => '̄',
        0xC6 => '̆',
        0xC7 => '̇',
        0xC8 => '̈',
        0xCA => '̊',
        0xCB => '̧',
        0xCD => '̋',
        0xCE => '̨',
        0xCF => '̌',
        _ => null,
    };
}
