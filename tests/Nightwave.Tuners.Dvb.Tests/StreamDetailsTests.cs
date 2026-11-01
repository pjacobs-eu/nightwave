using System.Text.Json;
using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb;
using Nightwave.Tuners.Dvb.Descrambling;
using Nightwave.Tuners.Dvb.Streaming;
using Nightwave.Tuners.Dvb.Transport;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>What the player's details panel is told about a SAT>IP channel, and how it is marked.</summary>
public sealed class StreamDetailsTests
{
    private static readonly TunerSignal Strong = new() { Level = 224, Locked = true, Quality = 15, Source = 1 };

    [Fact]
    public void A_clear_channel_shows_device_input_service_and_reception_without_descrambling()
    {
        var groups = StreamDetails.Build(Snapshot(scrambled: false) with
        {
            Transport = new TransportStatus { Signal = Strong, SignalReports = 12, DatagramsReceived = 9000, DatagramsLost = 3, Path = StreamPath.RtpUdp },
            RecentlyLost = 0,
            SharedWith = ["Bayern 3"],
        });

        Assert.Equal(["details.device", "details.input", "details.service", "details.reception"], groups.Select(group => group.TitleKey));

        var device = Items(groups, "details.device");
        Assert.Equal("KATHREIN SatIP Server 23:BD:33", device["details.device.name"].Value);
        Assert.Equal("EXIP-418", device["details.device.model"].Value);
        Assert.Equal("SAT>IP · DVBS2-8", device["details.device.type"].Value);
        Assert.Equal("1 / 2", device["details.device.tuners"].Value);
        Assert.Equal("details.transport.udp", device["details.device.transport"].ValueKey);

        var input = Items(groups, "details.input");
        Assert.Equal("src=1", input["details.input.source"].Value);
        Assert.Equal("19.2°E", input["details.input.position"].Value);
        Assert.Equal("11836 MHz H 27500 DVB-S · QPSK · FEC 3/4", input["details.input.transponder"].Value);
        Assert.Equal("Bayern 3", input["details.input.shared"].Value);

        var service = Items(groups, "details.service");
        Assert.Equal("sid 100 · tsid 1019 · onid 1", service["details.service.ids"].Value);
        Assert.Equal("257, 258", service["details.service.pids"].Value);
        Assert.Equal("details.value.no", service["details.service.scrambled"].ValueKey);

        var reception = Items(groups, "details.reception");
        Assert.Equal(TunedStreamDetailState.Good, reception["details.reception.lock"].State);
        Assert.Equal("224 / 255", reception["details.reception.level"].Value);
        Assert.Equal(TunedStreamDetailState.Good, reception["details.reception.level"].State);
        Assert.Equal(TunedStreamDetailState.Good, reception["details.reception.quality"].State);
        Assert.Equal("3 / 9003", reception["details.reception.lost"].Value);
        Assert.Equal(TunedStreamDetailState.Good, reception["details.reception.lost_recent"].State);
        Assert.False(reception.ContainsKey("details.reception.still_scrambled"));
    }

    [Fact]
    public void What_says_how_the_house_is_wired_is_marked_for_the_operator_only()
    {
        var groups = StreamDetails.Build(Snapshot(scrambled: true) with
        {
            Transport = new TransportStatus { Signal = Strong, SignalReports = 12, DatagramsReceived = 9000, DatagramsLost = 3, Path = StreamPath.RtpUdp },
            CanDescramble = true,
            Descrambling = new DescramblingStatus
            {
                Server = "192.168.1.30:9000",
                Connected = true,
                HasKeys = true,
                LastEcm = new EcmAnswer(0x098C, 0x1FF, 0, 312, "NDS", "sky_de", "local", "internal", 0),
            },
        });

        var lines = groups.SelectMany(group => group.Items).ToDictionary(item => item.LabelKey);

        // Addresses, the card a subscription is descrambled with, and the numbers of the stream.
        foreach (var key in new[]
                 {
                     "details.device.address", "details.device.tuners", "details.service.ids",
                     "details.service.pids", "details.descrambling.server", "details.descrambling.caid",
                     "details.descrambling.reader", "details.descrambling.source",
                 })
        {
            Assert.True(lines[key].IsOperatorOnly, $"'{key}' is shown to everybody watching.");
        }

        // What somebody watching came for: their own picture, and whether it is being opened.
        foreach (var key in new[]
                 {
                     "details.device.name", "details.input.transponder", "details.service.name",
                     "details.service.scrambled", "details.reception.level", "details.reception.quality",
                     "details.descrambling.state",
                 })
        {
            Assert.False(lines[key].IsOperatorOnly, $"'{key}' is hidden from whoever is watching.");
        }
    }

    [Fact]
    public void A_scrambled_channel_shows_how_it_is_descrambled_and_what_is_wrong()
    {
        var weak = new TunerSignal { Level = 100, Locked = true, Quality = 4 };
        var status = new DescramblingStatus
        {
            Server = "192.168.1.30:9000",
            Connected = true,
            ServerVersion = "CardServer 11.7xx",
            HasKeys = true,
            Algorithm = "CSA (iCAM mode 4)",
            LastEcm = new EcmAnswer(0x098C, 0x1FF, 0, 312, "NDS", "sky_de", "local", "internal", 0),
            EcmSectionsForwarded = 42,
        };

        var groups = StreamDetails.Build(Snapshot(scrambled: true) with
        {
            Transport = new TransportStatus { Signal = weak, SignalReports = 12, DatagramsReceived = 100, DatagramsLost = 7, Path = StreamPath.RtspInterleaved },
            RecentlyLost = 5,
            CanDescramble = true,
            Descrambling = status,
            StillScrambledPackets = 21,
        });

        Assert.Equal("details.descrambling", groups[^1].TitleKey);

        var reception = Items(groups, "details.reception");
        Assert.Equal(TunedStreamDetailState.Warning, reception["details.reception.level"].State);
        Assert.Equal(TunedStreamDetailState.Problem, reception["details.reception.quality"].State);
        Assert.Equal(TunedStreamDetailState.Warning, reception["details.reception.lost_recent"].State);
        Assert.Equal("21", reception["details.reception.still_scrambled"].Value);
        Assert.Equal(TunedStreamDetailState.Warning, reception["details.reception.still_scrambled"].State);

        var descrambling = Items(groups, "details.descrambling");
        Assert.Equal("details.descrambling.active", descrambling["details.descrambling.state"].ValueKey);
        Assert.Equal(TunedStreamDetailState.Good, descrambling["details.descrambling.state"].State);
        Assert.Equal("192.168.1.30:9000 · CardServer 11.7xx", descrambling["details.descrambling.server"].Value);
        Assert.Equal("0x098C (NDS)", descrambling["details.descrambling.caid"].Value);
        Assert.Equal("sky_de", descrambling["details.descrambling.reader"].Value);
        Assert.Equal("312 ms", descrambling["details.descrambling.ecm_time"].Value);
        Assert.Equal("CSA (iCAM mode 4)", descrambling["details.descrambling.algorithm"].Value);
        Assert.Equal("42", descrambling["details.descrambling.ecms"].Value);

        // A problem wins over the keys, with its text.
        var failing = StreamDetails.Build(Snapshot(scrambled: true) with
        {
            CanDescramble = true,
            Descrambling = status with { Problem = new TunerException(DvbProvider.Id, "descrambler.no_control_word", "No key for 10 seconds.", "Check the card.") },
        });
        var state = Items(failing, "details.descrambling")["details.descrambling.state"];
        Assert.Equal(("details.descrambling.problem", "No key for 10 seconds.", TunedStreamDetailState.Problem), (state.ValueKey, state.Value, state.State));

        // Without a descrambling server, only that.
        var unconfigured = Items(StreamDetails.Build(Snapshot(scrambled: true)), "details.descrambling");
        Assert.Equal("details.descrambling.not_configured", Assert.Single(unconfigured).Value.ValueKey);
        Assert.Equal(TunedStreamDetailState.Problem, unconfigured.Single().Value.State);
    }

    [Fact]
    public void No_lock_is_a_problem_and_a_device_without_reports_says_so()
    {
        var unlocked = Items(
            StreamDetails.Build(Snapshot(scrambled: false) with { Transport = new TransportStatus { Signal = new TunerSignal { Level = 200, Locked = false, Quality = 12 } } }),
            "details.reception");
        Assert.Equal(TunedStreamDetailState.Problem, unlocked["details.reception.lock"].State);
        Assert.Equal(TunedStreamDetailState.Problem, unlocked["details.reception.level"].State);

        var silent = Items(StreamDetails.Build(Snapshot(scrambled: false)), "details.reception");
        Assert.Equal("details.reception.no_report", silent["details.reception.lock"].ValueKey);
        Assert.False(silent.ContainsKey("details.reception.level"));
    }

    [Fact]
    public void Every_key_of_the_details_is_in_the_catalogue()
    {
        var english = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(EnglishCatalogue()))!;

        var status = new DescramblingStatus
        {
            Server = "descrambler:9000",
            SectionsDropped = 1,
            LastEcm = new EcmAnswer(0x1830, 0x200, 0, 50, "Nagra", "reader", "local", "internal", 0),
            Algorithm = "CSA",
        };

        var keys = new[]
            {
                Snapshot(scrambled: true) with { CanDescramble = true, Descrambling = status, RecentlyLost = 0, Transport = new TransportStatus { Signal = Strong, Path = StreamPath.RtspInterleaved } },
                Snapshot(scrambled: true) with { CanDescramble = true, Descrambling = status with { HasKeys = true }, Transport = new TransportStatus { Path = StreamPath.RtpUdp } },
                Snapshot(scrambled: true),
                Snapshot(scrambled: false) with { Transport = new TransportStatus { Signal = Strong with { Locked = false } } },
            }
            .SelectMany(StreamDetails.Build)
            .SelectMany(group => group.Items.Select(item => item.ValueKey).Append(group.TitleKey).Concat(group.Items.Select(item => item.LabelKey)))
            .OfType<string>()
            .Append("details.descrambling.problem")
            .Append("details.value.yes")
            .Append("details.transport.unknown")
            .Distinct();

        Assert.All(keys, key => Assert.True(english.ContainsKey(key), $"'{key}' is not in i18n/en.json."));
    }

    private static Dictionary<string, TunedStreamDetail> Items(IReadOnlyList<TunedStreamDetailGroup> groups, string title) =>
        groups.Single(group => group.TitleKey == title).Items.ToDictionary(item => item.LabelKey);

    private static StreamSnapshot Snapshot(bool scrambled) => new()
    {
        Device = SampleMux.Device() with
        {
            Name = "KATHREIN SatIP Server 23:BD:33",
            ReportedName = "KATHREIN SatIP Server 23:BD:33",
            ModelName = "EXIP-418",
            Capabilities = "DVBS2-8",
        },
        Request = new TuneRequest(SampleMux.Tuning, 1),
        Channel = SampleMux.Channel(SampleMux.ClearService, SampleMux.ClearPmtPid, scrambled: scrambled, name: "Das Erste HD"),
        TunersInUse = 1,
        Pids = [SampleMux.ClearAudioPid, SampleMux.ClearVideoPid],
        Scrambled = scrambled,
    };

    /// <summary>The catalogue lives with the interface, two folders up from these tests.</summary>
    private static string EnglishCatalogue([System.Runtime.CompilerServices.CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../../src/Nightwave.Server/i18n/en.json"));
}
