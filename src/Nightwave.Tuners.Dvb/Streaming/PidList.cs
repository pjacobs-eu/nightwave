namespace Nightwave.Tuners.Dvb.Streaming;

/// <summary>Pids for a log line, written out only when the line is.</summary>
internal readonly record struct PidList(IEnumerable<int> Pids)
{
    // An empty list said as nothing at all read "asked for pids ." - as though a word were missing.
    public override string ToString() => Pids.Any() ? string.Join(',', Pids.Order()) : "none";
}
