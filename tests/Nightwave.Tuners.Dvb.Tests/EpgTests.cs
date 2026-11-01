using Nightwave.Core.Storage;
using System.Globalization;
using System.Text;
using Nightwave.Tuners.Dvb.Epg;
using Nightwave.Tuners.Dvb.Mpeg.Si;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>
/// The guide from the broadcast: EIT sections as a multiplexer writes them, the language a
/// programme is shown in, and the store it is kept in.
/// </summary>
public sealed class EpgTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly string _directory = Directory.CreateTempSubdirectory("nightwave-epg-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Mjd_and_bcd_read_as_the_standard_example_says()
    {
        // EN 300 468, Annex C: 93/10/13 12:45:00 is coded as "C079124500".
        Assert.Equal(new DateTimeOffset(1993, 10, 13, 12, 45, 0, TimeSpan.Zero), DvbTime.Read([0xC0, 0x79, 0x12, 0x45, 0x00]));
        Assert.Equal(new TimeSpan(1, 45, 30), DvbTime.ReadDuration([0x01, 0x45, 0x30]));

        // All ones is "undefined", and not a date.
        Assert.Null(DvbTime.Read([0xFF, 0xFF, 0xFF, 0xFF, 0xFF]));

        var start = new DateTimeOffset(2026, 9, 19, 20, 15, 0, TimeSpan.Zero);
        Assert.Equal(start, DvbTime.Read(DvbTime.Write(start)));
    }

    [Fact]
    public void An_eit_section_gives_events_with_every_language_and_descriptor()
    {
        var start = new DateTimeOffset(2026, 9, 19, 20, 15, 0, TimeSpan.Zero);
        var descriptors = new List<byte>();
        descriptors.AddRange(EitBuilder.ShortEvent("ger", "Tatort", "Borowski und das Haus am Meer"));
        descriptors.AddRange(EitBuilder.ShortEvent("eng", "Crime Scene", "Borowski and the House by the Sea"));
        descriptors.AddRange(EitBuilder.Content(0x10, 0x11));
        descriptors.AddRange(EitBuilder.ParentalRating("DEU", 0x09));

        var section = EitBuilder.Section(0x50, 28106, 1101, 1, 3, (4711, start, TimeSpan.FromMinutes(90), [.. descriptors]));
        var eit = EventInformationTable.Parse(section);

        Assert.NotNull(eit);
        Assert.True(eit.IsSchedule);
        Assert.True(eit.IsActual);
        Assert.Equal(28106, eit.ServiceId);
        Assert.Equal(1101, eit.TransportStreamId);
        Assert.Equal(1, eit.OriginalNetworkId);
        Assert.Equal(3, eit.Version);

        var programme = Assert.Single(eit.Events);
        Assert.Equal(4711, programme.EventId);
        Assert.Equal(start, programme.Start);
        Assert.Equal(TimeSpan.FromMinutes(90), programme.Duration);
        Assert.Equal(new[] { "ger", "eng" }, programme.ShortEvents.Select(shortEvent => shortEvent.Language));
        Assert.Equal("Tatort", programme.ShortEvents[0].Name);
        Assert.Equal(new[] { 0x10, 0x11 }, programme.Content);
        Assert.Equal(12, programme.ParentalRatings["DEU"]);
    }

    [Fact]
    public void Extended_event_parts_are_joined_before_they_are_decoded()
    {
        // UTF-8 text cut in the middle of "ü" (0xC3 0xBC), as a broadcaster filling 255 bytes would.
        var text = Encoding.UTF8.GetBytes("Kommissar Borowski ermittelt in Kiel. Später mehr.");
        var cut = Array.IndexOf(text, (byte)0xC3) + 1;

        var first = EitBuilder.ExtendedEvent(0, 1, "ger", [("Regie", "Max Mustermann")], [0x15, .. text[..cut]]);
        var second = EitBuilder.ExtendedEvent(1, 1, "ger", [("Darsteller", "Axel Milberg")], [0x15, .. text[cut..]]);

        // Sent out of order on purpose: the descriptor number decides.
        var section = EitBuilder.Section(0x4E, 1, 2, 3, 0, (1, Now, TimeSpan.FromHours(1), [.. EitBuilder.ShortEvent("ger", "Tatort", ""), .. second, .. first]));
        var programme = Assert.Single(EventInformationTable.Parse(section)!.Events);
        var extended = Assert.Single(programme.ExtendedEvents);

        Assert.Equal("Kommissar Borowski ermittelt in Kiel. Später mehr.", extended.Text);
        Assert.Equal(new[] { ("Regie", "Max Mustermann"), ("Darsteller", "Axel Milberg") }, extended.Items);

        var epg = EpgEvent.From(programme, ["deu", "ger"])!;
        Assert.Equal("Kommissar Borowski ermittelt in Kiel. Später mehr.\nRegie: Max Mustermann\nDarsteller: Axel Milberg", epg.Description);
    }

    [Fact]
    public void The_preferred_language_is_picked_and_the_first_one_otherwise()
    {
        var programme = EitEvent.Create(
            1,
            Now,
            TimeSpan.FromHours(1),
            4,
            false,
            Nightwave.Tuners.Dvb.Mpeg.Psi.Descriptor.ReadLoop([.. EitBuilder.ShortEvent("fre", "Journal", "Info"), .. EitBuilder.ShortEvent("ger", "Nachrichten", "Aktuell")]));

        Assert.Equal("Nachrichten", EpgEvent.From(programme, EpgLanguages.Preferred(null, CultureInfo.GetCultureInfo("de-DE")))!.Title);
        Assert.Equal("Journal", EpgEvent.From(programme, EpgLanguages.Preferred(null, CultureInfo.GetCultureInfo("fr-FR")))!.Title);
        Assert.Equal("Journal", EpgEvent.From(programme, EpgLanguages.Preferred("spa", CultureInfo.InvariantCulture))!.Title);
    }

    [Fact]
    public void The_server_language_is_preferred_in_both_iso_spellings_then_english()
    {
        Assert.Equal(new[] { "deu", "ger", "eng" }, EpgLanguages.Preferred(null, CultureInfo.GetCultureInfo("de-DE")));
        Assert.Equal(new[] { "fra", "fre", "eng" }, EpgLanguages.Preferred("", CultureInfo.GetCultureInfo("fr-FR")));
        Assert.Equal(new[] { "ita", "eng" }, EpgLanguages.Preferred("ita, eng", CultureInfo.GetCultureInfo("de-DE")));
    }

    [Fact]
    public void Content_nibbles_become_readable_genres()
    {
        Assert.Equal(
            new[] { "Movie / Drama", "Comedy", "News / Current Affairs", "Documentary" },
            ContentGenres.From([0x14, 0x23, 0x10]));

        // Special characteristics are not genres; the live flag is read separately.
        Assert.Empty(ContentGenres.From([0xB3, 0xF0]));
    }

    [Fact]
    public void A_live_event_with_a_short_subtitle_and_a_description_maps_onto_the_guide()
    {
        var descriptors = Nightwave.Tuners.Dvb.Mpeg.Psi.Descriptor.ReadLoop(
        [
            .. EitBuilder.ShortEvent("eng", "Football", "Final"),
            .. EitBuilder.ExtendedEvent(0, 0, "eng", [], [.. "The final, live from Berlin."u8]),
            .. EitBuilder.Content(0x43, 0xB3),
        ]);

        var epg = EpgEvent.From(EitEvent.Create(9, Now, TimeSpan.FromHours(2), 4, false, descriptors), ["eng"])!;

        Assert.Equal("Football", epg.Title);
        Assert.Equal("Final", epg.Subtitle);
        Assert.Equal("The final, live from Berlin.", epg.Description);
        Assert.Equal(new[] { "Sports", "Football / Soccer" }, epg.Genres);
        Assert.True(epg.IsLive);
        Assert.Equal(Now.AddHours(2), epg.End);
    }

    [Fact]
    public void The_store_replaces_by_event_id_and_by_the_time_an_event_now_covers()
    {
        using var store = new EpgStore(new NightwaveDatabase(_directory), new ManualTime(Now));
        var service = new DvbServiceKey(1, 1019, 28006);

        store.Upsert(service, [Event(1, Now, 60, "News"), Event(2, Now.AddHours(1), 60, "Film")]);
        store.Upsert(service, [Event(1, Now, 60, "News, updated")]);

        // The schedule changed: one long event now where the film was.
        store.Upsert(service, [Event(3, Now.AddMinutes(90), 120, "Match")]);

        var programmes = store.Query(service, Now.AddHours(-1), Now.AddHours(5));

        Assert.Equal(new[] { "News, updated", "Match" }, programmes.Select(programme => programme.Title));
        Assert.Empty(store.Query(service, Now.AddHours(6), Now.AddHours(7)));
    }

    [Fact]
    public void The_store_keeps_a_window_and_prunes_what_fell_out_of_it()
    {
        var time = new ManualTime(Now);
        using var store = new EpgStore(new NightwaveDatabase(_directory), time);
        var service = new DvbServiceKey(1, 1019, 28006);

        store.Upsert(service, [Event(1, Now.AddDays(-3), 60, "Too old"), Event(2, Now.AddDays(20), 60, "Too far"), Event(3, Now, 60, "Now")]);
        Assert.Equal(new[] { "Now" }, store.Query(service, DateTimeOffset.MinValue, DateTimeOffset.MaxValue).Select(programme => programme.Title));

        time.Now = Now.AddDays(2);

        Assert.Equal(1, store.Prune());
        Assert.Empty(store.Services());
    }

    [Fact]
    public async Task The_store_survives_a_restart()
    {
        var service = new DvbServiceKey(1, 1019, 28006);

        using (var store = new EpgStore(new NightwaveDatabase(_directory), new ManualTime(Now)))
        {
            store.Upsert(service, [Event(1, Now, 60, "News")]);
            await store.FlushAsync(CancellationToken.None);
        }

        using var again = new EpgStore(new NightwaveDatabase(_directory), new ManualTime(Now));
        await again.EnsureLoadedAsync(CancellationToken.None);

        Assert.Equal("News", Assert.Single(again.Query(service, Now, Now.AddHours(1))).Title);
        Assert.Equal(new[] { service }, again.Services());
    }

    [Fact]
    public void The_collector_reads_the_eit_pid_and_skips_sections_it_has_seen()
    {
        using var store = new EpgStore(new NightwaveDatabase(_directory), new ManualTime(Now));
        var collector = new EitCollector(store, ["ger"]);

        var actual = EitBuilder.Section(0x50, 100, 1019, 1, 0, (1, Now, TimeSpan.FromHours(1), EitBuilder.ShortEvent("ger", "Tagesschau", "")));
        var other = EitBuilder.Section(0x60, 200, 1101, 1, 0, (7, Now, TimeSpan.FromHours(1), EitBuilder.ShortEvent("ger", "Rundschau", "")));
        var packets = TsBuilder.Join([.. TsBuilder.Packets(actual, 0x12, 0), .. TsBuilder.Packets(other, 0x12, 1), .. TsBuilder.Packets(actual, 0x12, 2)]);

        collector.Process(packets);

        Assert.Equal(2, collector.NewSections);
        Assert.Equal(new[] { new DvbServiceKey(1, 1019, 100), new DvbServiceKey(1, 1101, 200) }, collector.ServicesWithSchedule.OrderBy(key => key.ServiceId));
        Assert.Equal("Tagesschau", Assert.Single(store.Query(new DvbServiceKey(1, 1019, 100), Now, Now.AddHours(1))).Title);
        Assert.Equal("Rundschau", Assert.Single(store.Query(new DvbServiceKey(1, 1101, 200), Now, Now.AddHours(1))).Title);
    }

    [Fact]
    public void A_guide_id_round_trips()
    {
        var key = new DvbServiceKey(1, 1019, 28006);

        Assert.Equal("dvb:1.1019.28006", key.GuideId);
        Assert.True(DvbServiceKey.TryParse(key.GuideId, out var parsed));
        Assert.Equal(key, parsed);
        Assert.False(DvbServiceKey.TryParse("19.2E:1.1019.28006", out _));
    }

    private static EpgEvent Event(int id, DateTimeOffset start, int minutes, string title) => new()
    {
        EventId = id,
        Start = start,
        End = start.AddMinutes(minutes),
        Title = title,
    };
}
