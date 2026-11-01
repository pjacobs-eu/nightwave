using System.Globalization;
using Nightwave.Core.Configuration;
using Nightwave.Tuners.Dvb.Transport;
using Nightwave.Tuners.Dvb.Tuning;

namespace Nightwave.Tuners.Dvb.LinuxDvb;

/// <summary>What stands between a card and the sky: LNB, switch, single cable system, motor.</summary>
public sealed record DishSettings
{
    /// <summary>The oscillators of the LNB, in kHz. The defaults are a universal LNB.</summary>
    public int LowKhz { get; init; } = 9_750_000;

    public int HighKhz { get; init; } = 10_600_000;

    public int SwitchKhz { get; init; } = 11_700_000;

    public DvbSwitchKind Switch { get; init; } = DvbSwitchKind.Committed;

    public DvbUnicable Unicable { get; init; }

    public int UnicableSlot { get; init; }

    public int UnicableKhz { get; init; } = 1_210_000;

    public int? UnicablePin { get; init; }

    public DvbRotor Rotor { get; init; }

    /// <summary>The satellites of a motor, in tenths of a degree, east positive.</summary>
    public IReadOnlyList<int> RotorPositions { get; init; } = [];

    public double SiteLatitude { get; init; }

    public double SiteLongitude { get; init; }

    public int RotorMsPerDegree { get; init; } = 700;

    /// <summary>
    /// How many inputs detection tries. An input is a port of the switch, a satellite position of
    /// a single cable system, or a position of the motor.
    /// </summary>
    public int Inputs => (Rotor, Unicable, Switch) switch
    {
        (not DvbRotor.None, _, _) => RotorPositions.Count,
        (_, DvbUnicable.En50494, _) => 2,
        (_, DvbUnicable.En50607, _) => 4,
        (_, _, DvbSwitchKind.None) => 1,
        (_, _, DvbSwitchKind.ToneBurst) => 2,
        _ => 4,
    };

    /// <summary>Reads positions written <c>19.2E, 0.8W</c>. What cannot be read is left out.</summary>
    public static IReadOnlyList<int> ParsePositions(string? text)
    {
        var positions = new List<int>();

        foreach (var entry in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (entry.Length > 1
                && char.ToUpperInvariant(entry[^1]) is 'E' or 'W'
                && double.TryParse(entry[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees))
            {
                var tenths = (int)Math.Round(degrees * 10);

                positions.Add(char.ToUpperInvariant(entry[^1]) == 'E' ? tenths : -tenths);
            }
        }

        return positions;
    }
}

/// <summary>What it takes to tune a transponder on a card: its dish, then the demodulator.</summary>
internal sealed record DvbTuning
{
    /// <summary><see cref="DvbApi.Voltage13"/> or <see cref="DvbApi.Voltage18"/>; null where there is no LNB.</summary>
    public int? Voltage { get; init; }

    /// <summary><see cref="DvbApi.ToneOn"/> or <see cref="DvbApi.ToneOff"/>; null where there is no LNB.</summary>
    public int? Tone { get; init; }

    /// <summary>Each a <c>struct dvb_diseqc_master_cmd</c>, in the order they are sent.</summary>
    public IReadOnlyList<byte[]> Commands { get; init; } = [];

    /// <summary>The tone burst: 0 is satellite A, 1 satellite B.</summary>
    public int? Burst { get; init; }

    /// <summary>The command of a single cable system, sent at 18 V.</summary>
    public byte[]? Unicable { get; init; }

    /// <summary>The satellite a motor is being sent to.</summary>
    public int? RotorTenths { get; init; }

    public required IReadOnlyList<(uint Command, uint Value)> Properties { get; init; }

    public static DvbTuning For(TuningParameters tuning, int? source, DishSettings dish)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        ArgumentNullException.ThrowIfNull(dish);

        return tuning.System switch
        {
            DvbSystem.DvbS or DvbSystem.DvbS2 => Satellite(tuning, source, dish),
            DvbSystem.DvbC => Cable(tuning),
            _ => Terrestrial(tuning),
        };
    }

    private static DvbTuning Satellite(TuningParameters tuning, int? source, DishSettings dish)
    {
        var high = tuning.FrequencyKhz >= dish.SwitchKhz;
        var oscillator = high ? dish.HighKhz : dish.LowKhz;
        var horizontal = tuning.Polarization is Polarization.Horizontal or Polarization.CircularLeft;
        var intermediate = Math.Abs(tuning.FrequencyKhz - oscillator);
        var input = Math.Max(0, (source ?? 1) - 1);

        byte[]? unicable = null;
        var tuned = intermediate;

        if (dish.Unicable != DvbUnicable.None)
        {
            (unicable, tuned) = UnicableCommand(dish, intermediate, input, horizontal, high);
        }

        var s2 = tuning.System == DvbSystem.DvbS2;
        var properties = new List<(uint, uint)>
        {
            (DvbApi.DeliverySystem, s2 ? DvbApi.SysDvbS2 : DvbApi.SysDvbS),
            (DvbApi.Frequency, (uint)tuned),
            (DvbApi.Modulation, s2 ? SatelliteModulation(tuning.Modulation) : 0),
            (DvbApi.SymbolRate, (uint)(tuning.SymbolRate ?? 27500) * 1000),
            (DvbApi.InnerFec, Fec(tuning.Fec)),
            (DvbApi.Inversion, DvbApi.InversionAuto),
        };

        if (s2)
        {
            properties.Add((DvbApi.RollOff, RollOff(tuning.RollOff)));
            properties.Add((DvbApi.Pilot, tuning.Pilots switch
            {
                true => DvbApi.PilotOn,
                false => DvbApi.PilotOff,
                null => DvbApi.PilotAuto,
            }));
        }

        properties.Add((DvbApi.Tune, 0));

        if (unicable is not null)
        {
            // Everything goes through the user band: the cable stays at 13 V and without a tone.
            return new DvbTuning { Voltage = DvbApi.Voltage13, Tone = DvbApi.ToneOff, Unicable = unicable, Properties = properties };
        }

        var rotor = dish.Rotor != DvbRotor.None ? RotorTarget(dish, input) : null;

        return new DvbTuning
        {
            Voltage = horizontal ? DvbApi.Voltage18 : DvbApi.Voltage13,
            Tone = high ? DvbApi.ToneOn : DvbApi.ToneOff,
            Commands = rotor is { } target
                ? [RotorCommand(dish, input, target)]
                : source is not null ? SwitchCommands(dish, input, horizontal, high) : [],
            Burst = rotor is null && source is not null && dish.Switch == DvbSwitchKind.ToneBurst ? Math.Min(input, 1) : null,
            RotorTenths = rotor,
            Properties = properties,
        };
    }

    private static DvbTuning Cable(TuningParameters tuning) => new()
    {
        Properties =
        [
            (DvbApi.DeliverySystem, DvbApi.SysDvbCAnnexA),
            (DvbApi.Frequency, (uint)tuning.FrequencyKhz * 1000),
            (DvbApi.Modulation, CableModulation(tuning.Modulation)),
            (DvbApi.SymbolRate, (uint)(tuning.SymbolRate ?? 6900) * 1000),
            (DvbApi.InnerFec, DvbApi.FecAuto),
            (DvbApi.Inversion, DvbApi.InversionAuto),
            (DvbApi.Tune, 0),
        ],
    };

    private static DvbTuning Terrestrial(TuningParameters tuning)
    {
        var t2 = tuning.System == DvbSystem.DvbT2;
        var bandwidth = decimal.TryParse(tuning.Bandwidth, NumberStyles.Number, CultureInfo.InvariantCulture, out var mhz) ? mhz : 8;

        var properties = new List<(uint, uint)>
        {
            (DvbApi.DeliverySystem, t2 ? DvbApi.SysDvbT2 : DvbApi.SysDvbT),
            (DvbApi.Frequency, (uint)tuning.FrequencyKhz * 1000),
            (DvbApi.BandwidthHz, (uint)(bandwidth * 1_000_000)),
            (DvbApi.Modulation, CableModulation(tuning.Modulation)),
            (DvbApi.CodeRateHigh, Fec(tuning.Fec)),
            (DvbApi.CodeRateLow, DvbApi.FecAuto),
            (DvbApi.TransmissionMode, TransmissionMode(tuning.TransmissionMode)),
            (DvbApi.GuardInterval, GuardInterval(tuning.GuardInterval)),
            (DvbApi.Hierarchy, DvbApi.HierarchyAuto),
            (DvbApi.Inversion, DvbApi.InversionAuto),
        };

        if (t2 && tuning.Plp is { } plp)
        {
            properties.Add((DvbApi.StreamId, (uint)plp));
        }

        properties.Add((DvbApi.Tune, 0));

        return new DvbTuning { Properties = properties };
    }

    /// <summary>The command of a single cable system, and what the demodulator tunes to afterwards.</summary>
    private static (byte[] Command, int TunedKhz) UnicableCommand(DishSettings dish, int intermediateKhz, int position, bool horizontal, bool high)
    {
        var polarisation = horizontal ? 1 : 0;
        var band = high ? 1 : 0;
        var slot = dish.UnicableSlot;

        if (dish.Unicable == DvbUnicable.En50494)
        {
            var value = (((intermediateKhz / 1000) + 2 + (dish.UnicableKhz / 1000)) / 4) - 350;

            if (value is < 0 or >= 1023)
            {
                throw new TunerTransportException($"{intermediateKhz} kHz cannot be reached through the user band at {dish.UnicableKhz} kHz.");
            }

            var first = (byte)(((slot & 7) << 5) | ((position & 1) << 4) | (polarisation << 3) | (band << 2) | ((value >> 8) & 3));
            var tuned = ((value + 350) * 4000) - intermediateKhz;

            return (
                dish.UnicablePin is { } pin
                    ? Frame(0xE0, 0x10, 0x5C, first, (byte)value, (byte)pin)
                    : Frame(0xE0, 0x10, 0x5A, first, (byte)value),
                tuned);
        }

        var transponder = (int)Math.Round(intermediateKhz / 1000d) - 100;

        if (transponder is < 0 or > 2047)
        {
            throw new TunerTransportException($"{intermediateKhz} kHz is outside what EN 50607 can select.");
        }

        var data1 = (byte)(((slot & 0x1F) << 3) | ((transponder >> 8) & 7));
        var data3 = (byte)(((position & 0x3F) << 2) | (polarisation << 1) | band);

        return (
            dish.UnicablePin is { } code
                ? Frame(0x71, data1, (byte)transponder, data3, (byte)code)
                : Frame(0x70, data1, (byte)transponder, data3),
            dish.UnicableKhz);
    }

    private static List<byte[]> SwitchCommands(DishSettings dish, int input, bool horizontal, bool high)
    {
        var port = Math.Min(input, 3);

        return dish.Switch switch
        {
            DvbSwitchKind.Committed => [Frame(0xE0, 0x10, 0x38, (byte)(0xF0 | (port << 2) | (horizontal ? 2 : 0) | (high ? 1 : 0)))],
            DvbSwitchKind.Uncommitted => [Frame(0xE0, 0x10, 0x39, (byte)(0xF0 | port))],
            _ => [],
        };
    }

    private static int? RotorTarget(DishSettings dish, int input) =>
        input < dish.RotorPositions.Count
            ? dish.RotorPositions[input]
            : throw new TunerTransportException($"The motor has no position {input + 1}. Add it to the list of positions in the tuner settings.");

    private static byte[] RotorCommand(DishSettings dish, int input, int tenths)
    {
        if (dish.Rotor == DvbRotor.StoredPositions)
        {
            return Frame(0xE0, 0x31, 0x6B, (byte)(input + 1));
        }

        var value = UsalsValue(UsalsAngle(dish.SiteLatitude, dish.SiteLongitude, tenths / 10d));

        return Frame(0xE0, 0x31, 0x6E, (byte)(value >> 8), (byte)value);
    }

    /// <summary>
    /// The angle a polar mount turns to, in degrees, positive towards the east seen from the north.
    /// The satellite's direction seen from a place on the ellipsoid, turned into the hour angle
    /// of the mount.
    /// </summary>
    public static double UsalsAngle(double latitude, double longitude, double satellite)
    {
        const double Flattening = 1 / 298.257;
        const double OrbitKm = 42164.57;
        const double EquatorKm = 6378.14;

        static double Radians(double degrees) => degrees * Math.PI / 180;

        var sinLatitude = Math.Sin(Radians(latitude));
        var cosLatitude = Math.Cos(Radians(latitude));
        var radius = EquatorKm / Math.Sqrt(1 - (Flattening * (2 - Flattening) * sinLatitude * sinLatitude));
        var apart = Radians(satellite - longitude);

        var outward = (OrbitKm * Math.Cos(apart)) - (radius * cosLatitude);
        var east = OrbitKm * Math.Sin(apart);
        var axial = -radius * (1 - Flattening) * (1 - Flattening) * sinLatitude;

        var north = (-outward * sinLatitude) + (axial * cosLatitude);
        var up = (outward * cosLatitude) + (axial * sinLatitude);

        // The hour angle is what a polar mount turns about its axis, parallel to the earth's.
        var equatorial = (up * cosLatitude) - (north * sinLatitude);
        var angle = Math.Atan2(east, equatorial) * 180 / Math.PI;

        return latitude >= 0 ? angle : -angle;
    }

    /// <summary>The two bytes of a USALS command: a direction, whole degrees, and a coded fraction.</summary>
    public static int UsalsValue(double angle)
    {
        byte[] fractions = [0x00, 0x02, 0x03, 0x05, 0x06, 0x08, 0x0A, 0x0B, 0x0D, 0x0E];

        var tenths = (int)Math.Round(Math.Abs(angle) * 10);

        return (angle < 0 ? 0xD000 : 0xE000) | (((tenths / 10) * 0x10) + fractions[tenths % 10]);
    }

    /// <summary>How long a motor needs. From an unknown position, as if it had far to go.</summary>
    public static TimeSpan RotorTravel(int? fromTenths, int toTenths, int millisecondsPerDegree)
    {
        const double UnknownDegrees = 30;

        var degrees = fromTenths is { } from ? Math.Abs(toTenths - from) / 10d : UnknownDegrees;

        return TimeSpan.FromMilliseconds((degrees * millisecondsPerDegree) + 1000);
    }

    /// <summary>A <c>struct dvb_diseqc_master_cmd</c>: up to six bytes, then how many.</summary>
    private static byte[] Frame(params byte[] message)
    {
        var frame = new byte[7];

        message.CopyTo(frame, 0);
        frame[6] = (byte)message.Length;

        return frame;
    }

    private static uint SatelliteModulation(string? modulation) => modulation switch
    {
        "8psk" => 9,
        "16apsk" => 10,
        "32apsk" => 11,
        _ => 0,
    };

    private static uint TransmissionMode(string? mode) => mode switch
    {
        "2k" => 0,
        "8k" => 1,
        "4k" => 3,
        "1k" => 4,
        "16k" => 5,
        "32k" => 6,
        _ => DvbApi.TransmissionModeAuto,
    };

    private static uint GuardInterval(string? interval) => interval switch
    {
        "132" => 0,
        "116" => 1,
        "18" => 2,
        "14" => 3,
        "1128" => 5,
        "19128" => 6,
        "19256" => 7,
        _ => DvbApi.GuardIntervalAuto,
    };

    private static uint CableModulation(string? modulation) => modulation switch
    {
        "qpsk" => 0,
        "16qam" => 1,
        "32qam" => 2,
        "64qam" => 3,
        "128qam" => 4,
        "256qam" => 5,
        _ => DvbApi.ModulationQamAuto,
    };

    private static uint Fec(string? fec) => fec switch
    {
        "12" => 1,
        "23" => 2,
        "34" => 3,
        "45" => 4,
        "56" => 5,
        "67" => 6,
        "78" => 7,
        "89" => 8,
        "35" => 10,
        "910" => 11,
        "25" => 12,
        "13" => 13,
        "14" => 14,
        _ => DvbApi.FecAuto,
    };

    private static uint RollOff(string? rollOff) => rollOff switch
    {
        "0.35" => 0,
        "0.20" or "0.2" => 1,
        "0.25" => 2,
        _ => DvbApi.RollOffAuto,
    };

    /// <summary>Reads a card's measurements into a signal report. What a driver does not measure stays unmeasured.</summary>
    public static TunerSignal Signal(
        uint status,
        (byte Scale, long Value)? strength,
        (byte Scale, long Value)? carrierToNoise,
        long? errorBits,
        long? totalBits)
    {
        const double WeakestDbm = -90;
        const double StrongestDbm = -20;

        var locked = (status & DvbApi.StatusLock) != 0;
        double? snr = carrierToNoise is { Scale: DvbApi.ScaleDecibel } noise ? noise.Value / 1000d : null;

        double? level = strength switch
        {
            { Scale: DvbApi.ScaleRelative } relative => relative.Value / 65535d,
            { Scale: DvbApi.ScaleDecibel } power => ((power.Value / 1000d) - WeakestDbm) / (StrongestDbm - WeakestDbm),
            _ => null,
        };

        double? quality = carrierToNoise switch
        {
            { Scale: DvbApi.ScaleRelative } relative => relative.Value / 65535d,
            { Scale: DvbApi.ScaleDecibel } => snr / 15d,
            _ => null,
        };

        return new TunerSignal
        {
            Locked = locked,

            // Zero is read as no carrier at all: where nothing is measured, the status says so.
            Level = level is { } known ? (int)Math.Round(Math.Clamp(known, 0, 1) * 255) : (status & DvbApi.StatusSignal) != 0 ? 1 : 0,
            Quality = quality is { } good ? (int)Math.Round(Math.Clamp(good, 0, 1) * 15) : 0,
            Measured = level is not null,
            LevelRaw = strength switch
            {
                { Scale: DvbApi.ScaleDecibel } power => string.Create(CultureInfo.InvariantCulture, $"{power.Value / 1000d:0.0} dBm"),
                _ => null,
            },
            QualityRaw = snr is { } decibel ? string.Create(CultureInfo.InvariantCulture, $"{decibel:0.0} dB") : null,
            SnrDb = snr,
            BitErrorRate = errorBits is { } errors && totalBits is > 0 and { } total ? (double)errors / total : null,
        };
    }
}
