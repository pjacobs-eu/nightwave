using System.Diagnostics;
using System.Globalization;
using Nightwave.Tuners.Dvb.Mpeg;
using Nightwave.Tuners.Dvb.Mpeg.Si;

namespace Nightwave.Tuners.Dvb.Epg;

/// <summary>
/// Reads the EIT out of whatever a tuner delivers and hands the programmes to the store. One
/// per tuned transponder: section assembly is per pid and per stream.
/// </summary>
/// <remarks>
/// EIT sections repeat over and over with nothing new in them. Each one is remembered by table,
/// service, section and version, and a repeat is dropped before it is parsed - which is also how
/// a grabber knows it has seen everything: no new section for a while.
/// </remarks>
internal sealed class EitCollector
{
    private readonly EpgStore _store;
    private readonly IReadOnlyList<string> _languages;
    private readonly SectionAssembler _assembler = new();
    private readonly Dictionary<(int Table, int Network, int TransportStream, int Service, int Section), int> _seen = new();
    private readonly HashSet<DvbServiceKey> _withSchedule = [];
    private readonly Stopwatch _sinceNew = Stopwatch.StartNew();

    public EitCollector(EpgStore store, IReadOnlyList<string> languages)
    {
        _store = store;
        _languages = languages;
    }

    /// <summary>Sections that were new: not seen before, or seen in an older version.</summary>
    public int NewSections { get; private set; }

    /// <summary>How long ago the last new section arrived.</summary>
    public TimeSpan SinceLastNewSection => _sinceNew.Elapsed;

    /// <summary>Services a schedule table arrived for, actual or other.</summary>
    public IReadOnlyCollection<DvbServiceKey> ServicesWithSchedule => _withSchedule;

    /// <summary>Takes whole packets; only the EIT pid is looked at.</summary>
    public void Process(ReadOnlySpan<byte> packets)
    {
        for (var offset = 0; offset + TransportPacket.Size <= packets.Length; offset += TransportPacket.Size)
        {
            var packet = packets.Slice(offset, TransportPacket.Size);

            if (TransportPacket.Pid(packet) == EventInformationTable.Pid)
            {
                _assembler.Push(packet, OnSection);
            }
        }
    }

    private void OnSection(ReadOnlySpan<byte> section)
    {
        if (!EventInformationTable.IsEitTable(section[0]) || section.Length < 14)
        {
            return;
        }

        // Identify the section before parsing it: most are repeats.
        var service = (section[3] << 8) | section[4];
        var version = (section[5] >> 1) & 0x1F;
        var transportStream = (section[8] << 8) | section[9];
        var network = (section[10] << 8) | section[11];
        var key = (section[0], network, transportStream, service, (int)section[6]);

        if (_seen.TryGetValue(key, out var seenVersion) && seenVersion == version)
        {
            return;
        }

        if (EventInformationTable.Parse(section) is not { } table)
        {
            return;
        }

        _seen[key] = version;
        NewSections++;
        _sinceNew.Restart();

        var serviceKey = new DvbServiceKey(table.OriginalNetworkId, table.TransportStreamId, table.ServiceId);

        if (table.IsSchedule)
        {
            _withSchedule.Add(serviceKey);
        }

        var events = table.Events
            .Select(source => EpgEvent.From(source, _languages))
            .OfType<EpgEvent>()
            .ToList();

        if (events.Count > 0)
        {
            _store.Upsert(serviceKey, events);
        }
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{NewSections} sections, {_withSchedule.Count} services with a schedule");
}

/// <summary>
/// The guide from the broadcast as the rest of the plugin sees it: the store, whether
/// collecting is switched on, and which languages to keep.
/// </summary>
internal sealed class EpgService
{
    private readonly DvbSettingsProvider _settings;

    public EpgService(EpgStore store, DvbSettingsProvider settings)
    {
        Store = store;
        _settings = settings;
    }

    public EpgStore Store { get; }

    /// <summary>
    /// Whether tuners collect the guide as they go. Read from the settings last loaded, since a
    /// tuner asks from inside its start and cannot wait for a read.
    /// </summary>
    public bool IsEnabled => _settings.Current?.EpgEnabled ?? true;

    public IReadOnlyList<string> Languages => EpgLanguages.Preferred(_settings.Current?.EpgLanguages, CultureInfo.CurrentUICulture);

    public EitCollector CreateCollector() => new(Store, Languages);
}
