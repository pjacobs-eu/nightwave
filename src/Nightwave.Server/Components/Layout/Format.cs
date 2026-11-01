using System.Globalization;
using Nightwave.Core.Analysis;
using Nightwave.Core.Tuning;

namespace Nightwave.Server.Components.Layout;

/// <summary>How figures read in the interface.</summary>
internal static class Format
{
    public static string BitRate(double bitsPerSecond) => bitsPerSecond >= 1_000_000
        ? $"{bitsPerSecond / 1_000_000:0.00} Mbit/s"
        : $"{bitsPerSecond / 1000:0} kbit/s";

    public static string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    public static string Pid(int pid) => string.Create(CultureInfo.InvariantCulture, $"{pid} (0x{pid:X})");

    public static string? Modulation(string? modulation) => modulation?.ToUpperInvariant();

    public static string Duration(TimeSpan span) => span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes}:{span.Seconds:00}";

    public static string Polarisation(string? code, Texts t) => code switch
    {
        "H" => t["facts.pol.h"],
        "V" => t["facts.pol.v"],
        "L" => t["facts.pol.l"],
        "R" => t["facts.pol.r"],
        _ => "–",
    };

    public static string? Audio(AudioFormat? format)
    {
        if (format is null)
        {
            return null;
        }

        var parts = new List<string>();

        if (format.Channels is { } channels)
        {
            parts.Add(channels);
        }

        if (format.SampleRate is { } rate)
        {
            parts.Add($"{rate / 1000d:0.#} kHz");
        }

        if (format.BitRate is { } bits)
        {
            parts.Add($"{bits} kbit/s");
        }

        return string.Join(" · ", parts);
    }

    public static string CaSystems(IEnumerable<int> systems) => string.Join(", ", systems.Select(DvbNames.CaSystemWithId));
}
