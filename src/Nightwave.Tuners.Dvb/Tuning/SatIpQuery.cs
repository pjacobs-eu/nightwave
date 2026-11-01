using System.Globalization;
using System.Text;

namespace Nightwave.Tuners.Dvb.Tuning;

/// <summary>
/// Writes the query string SAT>IP tunes with (SAT>IP 1.2.2, 3.5.11), as it goes into an RTSP
/// SETUP.
/// </summary>
internal static class SatIpQuery
{
    /// <summary>
    /// The tuning part of the query. <paramref name="source"/> is the device input (<c>src</c>),
    /// which satellite tuning needs and cable and terrestrial do not have.
    /// </summary>
    public static string Tuning(TuningParameters tuning, int? source)
    {
        ArgumentNullException.ThrowIfNull(tuning);

        var query = new StringBuilder();

        void Add(string key, string? value)
        {
            if (value is null)
            {
                return;
            }

            if (query.Length > 0)
            {
                query.Append('&');
            }

            query.Append(key).Append('=').Append(value);
        }

        switch (tuning.System)
        {
            case DvbSystem.DvbS or DvbSystem.DvbS2:
                var s2 = tuning.System == DvbSystem.DvbS2;
                var modulation = tuning.Modulation ?? "qpsk";

                Add("src", (source ?? 1).ToString(CultureInfo.InvariantCulture));
                Add("freq", tuning.FrequencyMhz);
                Add("pol", tuning.Polarization switch
                {
                    Polarization.Vertical => "v",
                    Polarization.CircularLeft => "l",
                    Polarization.CircularRight => "r",
                    _ => "h",
                });

                // DVB-S has one roll-off, no pilots and QPSK only; the query still wants them.
                Add("ro", s2 ? tuning.RollOff ?? "0.35" : "0.35");
                Add("msys", s2 ? "dvbs2" : "dvbs");
                Add("mtype", s2 ? modulation : "qpsk");
                Add("plts", s2 && (tuning.Pilots ?? modulation != "qpsk") ? "on" : "off");
                Add("sr", tuning.SymbolRate?.ToString(CultureInfo.InvariantCulture));
                Add("fec", tuning.Fec);
                break;

            case DvbSystem.DvbC:
                Add("freq", tuning.FrequencyMhz);
                Add("msys", "dvbc");
                Add("mtype", tuning.Modulation);
                Add("sr", tuning.SymbolRate?.ToString(CultureInfo.InvariantCulture));
                break;

            default:
                Add("freq", tuning.FrequencyMhz);
                Add("bw", tuning.Bandwidth ?? "8");
                Add("msys", tuning.System == DvbSystem.DvbT2 ? "dvbt2" : "dvbt");
                Add("tmode", tuning.TransmissionMode);
                Add("mtype", tuning.Modulation);
                Add("gi", tuning.GuardInterval);
                Add("fec", tuning.Fec);

                if (tuning.System == DvbSystem.DvbT2)
                {
                    Add("plp", tuning.Plp?.ToString(CultureInfo.InvariantCulture));
                    Add("t2id", tuning.T2SystemId?.ToString(CultureInfo.InvariantCulture));
                }

                break;
        }

        return query.ToString();
    }

    /// <summary>A pid list the way SAT>IP writes it: ascending, comma separated, <c>none</c> for none.</summary>
    public static string Pids(IEnumerable<int> pids)
    {
        ArgumentNullException.ThrowIfNull(pids);

        var sorted = pids.Distinct().Order().ToList();

        return sorted.Count == 0
            ? "none"
            : string.Join(',', sorted.Select(pid => pid.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>What goes after the stream's address to change the pids.</summary>
    /// <param name="fullList">Whether the server only takes the whole list.</param>
    public static string PidChange(IReadOnlyCollection<int> wanted, IReadOnlyCollection<int> added, IReadOnlyCollection<int> removed, bool fullList)
    {
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(removed);

        if (fullList)
        {
            return $"pids={Pids(wanted)}";
        }

        var changes = new List<string>(2);

        if (added.Count > 0)
        {
            changes.Add($"addpids={Pids(added)}");
        }

        if (removed.Count > 0)
        {
            changes.Add($"delpids={Pids(removed)}");
        }

        return string.Join('&', changes);
    }

    /// <summary>The full query: tuning and pids.</summary>
    public static string Build(TuningParameters tuning, int? source, IEnumerable<int> pids) =>
        $"{Tuning(tuning, source)}&pids={Pids(pids)}";
}
