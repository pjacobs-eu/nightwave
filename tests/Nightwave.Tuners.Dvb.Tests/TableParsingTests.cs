using System.Text;
using Nightwave.Tuners.Dvb.Mpeg.Psi;
using Nightwave.Tuners.Dvb.Mpeg.Si;
using Nightwave.Tuners.Dvb.Tuning;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>
/// PAT, PMT, CAT, SDT and NIT, from sections written byte by byte after ISO/IEC 13818-1 and
/// EN 300 468.
/// </summary>
public sealed class TableParsingTests
{
    [Fact]
    public void The_pat_lists_programs_and_says_where_the_nit_is()
    {
        var pat = ProgramAssociationTable.Parse(TsBuilder.Pat(1101, 7, (0, 0x0010), (28006, 0x0064), (28007, 0x0065)));

        Assert.NotNull(pat);
        Assert.Equal(1101, pat.TransportStreamId);
        Assert.Equal(7, pat.Version);
        Assert.Equal(0x0010, pat.NetworkPid);
        Assert.Equal(new Dictionary<int, int> { [28006] = 0x64, [28007] = 0x65 }, pat.Programs);
    }

    [Fact]
    public void The_pmt_tells_codecs_apart_by_stream_type_and_descriptors()
    {
        var section = TsBuilder.Pmt(
            28006,
            2,
            0x0101,
            TsBuilder.CaDescriptor(0x1830, 0x0200),
            (0x1B, 0x0101, []),
            (0x24, 0x0102, []),
            (0x02, 0x0103, []),
            (0x03, 0x0104, TsBuilder.Descriptor(0x0A, (byte)'d', (byte)'e', (byte)'u', 0)),
            (0x0F, 0x0105, []),
            (0x06, 0x0106, [.. TsBuilder.Descriptor(0x6A, 0), .. TsBuilder.Descriptor(0x0A, (byte)'e', (byte)'n', (byte)'g', 0)]),
            (0x06, 0x0107, TsBuilder.Descriptor(0x7A, 0)),
            (0x06, 0x0108, TsBuilder.Descriptor(0x59, (byte)'d', (byte)'e', (byte)'u', 0x10, 0, 1, 0, 1)),
            (0x06, 0x0109, TsBuilder.Descriptor(0x56, (byte)'d', (byte)'e', (byte)'u', 0x09, 0x50)),
            (0x05, 0x010A, []));

        var pmt = ProgramMapTable.Parse(section);

        Assert.NotNull(pmt);
        Assert.Equal(28006, pmt.ProgramNumber);
        Assert.Equal(2, pmt.Version);
        Assert.Equal(0x0101, pmt.PcrPid);
        Assert.Equal(section, pmt.Section);

        Assert.Equal(
            [
                (StreamKind.Video, "h264"),
                (StreamKind.Video, "hevc"),
                (StreamKind.Video, "mpeg2video"),
                (StreamKind.Audio, "mp2"),
                (StreamKind.Audio, "aac"),
                (StreamKind.Audio, "ac3"),
                (StreamKind.Audio, "eac3"),
                (StreamKind.Subtitles, "dvb_subtitle"),
                (StreamKind.Teletext, "dvb_teletext"),
                (StreamKind.Data, null),
            ],
            pmt.Streams.Select(stream => (stream.Kind, stream.Codec)).ToArray());

        Assert.Equal("deu", pmt.Streams[3].Language);
        Assert.Equal("eng", pmt.Streams[5].Language);
        Assert.Equal("deu", pmt.Streams[7].Language);

        var ca = Assert.Single(pmt.ConditionalAccess);
        Assert.Equal(0x1830, ca.SystemId);
        Assert.Equal(0x0200, ca.Pid);
        Assert.True(pmt.HasConditionalAccess);
    }

    [Fact]
    public void Ca_descriptors_on_a_stream_count_as_well()
    {
        var pmt = ProgramMapTable.Parse(TsBuilder.Pmt(1, 0, 0x100, [], (0x1B, 0x100, TsBuilder.CaDescriptor(0x0D95, 0x0300))));

        Assert.NotNull(pmt);
        Assert.Equal(0x0300, Assert.Single(pmt.ConditionalAccess).Pid);
    }

    [Fact]
    public void The_cat_lists_the_emm_pids()
    {
        var section = TsBuilder.Section(0x01, 0xFFFF, 1, [.. TsBuilder.CaDescriptor(0x1830, 0x0400), .. TsBuilder.CaDescriptor(0x0500, 0x0401)]);
        var cat = ConditionalAccessTable.Parse(section);

        Assert.NotNull(cat);
        Assert.Equal([(0x1830, 0x0400), (0x0500, 0x0401)], cat.Systems.Select(system => (system.SystemId, system.Pid)).ToArray());
    }

    [Fact]
    public void The_sdt_names_services_and_says_which_are_scrambled()
    {
        var section = TsBuilder.Sdt(
            1101,
            1,
            4,
            (28006, false, TsBuilder.ServiceDescriptor(0x19, "ARD", "Das Erste HD")),
            (28007, true, TsBuilder.ServiceDescriptor(0x02, "SKY", "Radio")),
            (28008, false, []));

        var sdt = ServiceDescriptionTable.Parse(section);

        Assert.NotNull(sdt);
        Assert.True(sdt.IsActual);
        Assert.Equal(1101, sdt.TransportStreamId);
        Assert.Equal(1, sdt.OriginalNetworkId);
        Assert.Equal(3, sdt.Services.Count);

        var first = sdt.Services[0];
        Assert.Equal("Das Erste HD", first.Name);
        Assert.Equal("ARD", first.ProviderName);
        Assert.True(first.IsTelevision);
        Assert.False(first.IsScrambled);

        Assert.True(sdt.Services[1].IsRadio);
        Assert.True(sdt.Services[1].IsScrambled);

        Assert.Null(sdt.Services[2].Name);
        Assert.Null(sdt.Services[2].ServiceType);
    }

    [Fact]
    public void A_service_name_in_utf8_comes_through()
    {
        var name = Encoding.UTF8.GetBytes("Первый канал");
        var descriptor = TsBuilder.Descriptor(0x48, [0x01, 0, (byte)(name.Length + 1), 0x15, .. name]);
        var sdt = ServiceDescriptionTable.Parse(TsBuilder.Sdt(1, 2, 0, (5, false, descriptor)));

        Assert.Equal("Первый канал", sdt!.Services[0].Name);
    }

    [Fact]
    public void The_nit_turns_satellite_delivery_descriptors_into_tuning()
    {
        var section = TsBuilder.Nit(
            1,
            12,
            TsBuilder.Descriptor(0x40, [.. "ASTRA"u8]),
            (1019, 1, [.. TsBuilder.SatelliteDelivery(11836.00m, 192, horizontal: true, s2: false, 27500, 3), .. TsBuilder.Descriptor(0x83, 0x6D, 0x66, 0xFC, 0x01)]),
            (1010, 1, TsBuilder.SatelliteDelivery(11494.00m, 192, horizontal: true, s2: true, 22000, 2)));

        var nit = NetworkInformationTable.Parse(section);

        Assert.NotNull(nit);
        Assert.Equal("ASTRA", nit.NetworkName);
        Assert.Equal(2, nit.TransportStreams.Count);

        var first = nit.TransportStreams[0];
        Assert.Equal(1019, first.TransportStreamId);
        Assert.Equal(192, first.OrbitalPosition);
        Assert.Equal(
            new TuningParameters
            {
                System = DvbSystem.DvbS,
                FrequencyKhz = 11836000,
                Polarization = Polarization.Horizontal,
                SymbolRate = 27500,
                Modulation = "qpsk",
                RollOff = "0.35",
                Fec = "34",
            },
            first.Tuning);
        Assert.Equal(1, first.LogicalChannels[0x6D66]);

        var second = nit.TransportStreams[1].Tuning!;
        Assert.Equal(DvbSystem.DvbS2, second.System);
        Assert.Equal("8psk", second.Modulation);
        Assert.Equal("23", second.Fec);
        Assert.Equal(22000, second.SymbolRate);
    }

    [Fact]
    public void The_nit_reads_cable_and_terrestrial_delivery_descriptors()
    {
        // Terrestrial: 506 MHz in 10 Hz steps, 8 MHz, 64-QAM, 2/3, guard 1/4, 8k.
        var centre = 506_000_000 / 10;
        var terrestrial = TsBuilder.Descriptor(
            0x5A,
            (byte)(centre >> 24), (byte)(centre >> 16), (byte)(centre >> 8), (byte)centre,
            0x1F, 0x81, 0x1A, 0xFF, 0xFF, 0xFF, 0xFF);
        var t2 = TsBuilder.Descriptor(0x7F, 0x04, 0x01, 0x80, 0x01);

        var nit = NetworkInformationTable.Parse(TsBuilder.Nit(
            61441,
            0,
            [],
            (1, 61441, TsBuilder.CableDelivery(346m, 6900, 5)),
            (2, 8468, [.. terrestrial, .. t2])));

        var cable = nit!.TransportStreams[0].Tuning!;
        Assert.Equal(DvbSystem.DvbC, cable.System);
        Assert.Equal(346000, cable.FrequencyKhz);
        Assert.Equal(6900, cable.SymbolRate);
        Assert.Equal("256qam", cable.Modulation);

        var t2Tuning = nit.TransportStreams[1].Tuning!;
        Assert.Equal(DvbSystem.DvbT2, t2Tuning.System);
        Assert.Equal(506000, t2Tuning.FrequencyKhz);
        Assert.Equal("8", t2Tuning.Bandwidth);
        Assert.Equal("64qam", t2Tuning.Modulation);
        Assert.Equal("23", t2Tuning.Fec);
        Assert.Equal("14", t2Tuning.GuardInterval);
        Assert.Equal("8k", t2Tuning.TransmissionMode);
        Assert.Equal(1, t2Tuning.Plp);
        Assert.Equal(0x8001, t2Tuning.T2SystemId);
    }

    [Fact]
    public void A_table_that_is_not_current_is_not_used()
    {
        var section = TsBuilder.Pat(1, 0, (1, 0x100));
        section[5] &= 0xFE;
        Nightwave.Tuners.Dvb.Mpeg.MpegCrc32.Seal(section);

        Assert.Null(ProgramAssociationTable.Parse(section));
    }
}
