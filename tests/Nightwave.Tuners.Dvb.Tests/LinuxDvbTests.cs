using System.Buffers.Binary;
using Nightwave.Core.Configuration;
using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.LinuxDvb;
using Nightwave.Tuners.Dvb.Transport;
using Nightwave.Tuners.Dvb.Tuning;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>
/// The driver for cards in this machine, against a kernel that only writes down what it is told.
/// That shows what a card would be asked to do; whether a card does it takes a card.
/// </summary>
public sealed class LinuxDvbTests
{
    private static readonly DishSettings Universal = new();

    private static TuningParameters Satellite(int mhz, Polarization polarization, DvbSystem system = DvbSystem.DvbS) => new()
    {
        System = system,
        FrequencyKhz = mhz * 1000,
        Polarization = polarization,
        SymbolRate = 27500,
        Modulation = system == DvbSystem.DvbS2 ? "8psk" : "qpsk",
        Fec = "34",
    };

    private static TuningParameters Cable => new() { System = DvbSystem.DvbC, FrequencyKhz = 330_000, SymbolRate = 6900, Modulation = "256qam" };

    private static uint Value(DvbTuning tuning, uint command) => tuning.Properties.Single(property => property.Command == command).Value;

    /// <summary>The request codes as the kernel headers work them out on x86-64 and arm64.</summary>
    [Fact]
    public void The_requests_are_those_of_the_kernel()
    {
        Assert.Equal(0x80A86F3DUL, DvbApi.FeGetInfo);
        Assert.Equal(0x40076F3FUL, DvbApi.FeSendDiseqc);
        Assert.Equal(0x6F42UL, DvbApi.FeSetTone);
        Assert.Equal(0x6F43UL, DvbApi.FeSetVoltage);
        Assert.Equal(0x80046F45UL, DvbApi.FeReadStatus);
        Assert.Equal(0x40106F52UL, DvbApi.FeSetProperty);
        Assert.Equal(0x80106F53UL, DvbApi.FeGetProperty);
        Assert.Equal(0x40146F2CUL, DvbApi.DmxSetPesFilter);
        Assert.Equal(0x6F2DUL, DvbApi.DmxSetBufferSize);
        Assert.Equal(0x40026F33UL, DvbApi.DmxAddPid);
        Assert.Equal(0x40026F34UL, DvbApi.DmxRemovePid);
    }

    [Fact]
    public void Properties_are_laid_out_the_way_the_packed_structure_is()
    {
        var bytes = DvbApi.Properties([(DvbApi.Frequency, 0x01020304), (DvbApi.Tune, 0)]);

        Assert.Equal(2 * 76, bytes.Length);
        Assert.Equal(3u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(0x01020304u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(76)));
    }

    [Fact]
    public void A_low_band_transponder_uses_the_low_oscillator_without_the_tone()
    {
        var tuning = DvbTuning.For(Satellite(11362, Polarization.Horizontal), null, Universal);

        Assert.Equal((11_362_000u - 9_750_000u), Value(tuning, DvbApi.Frequency));
        Assert.Equal(DvbApi.ToneOff, tuning.Tone);
        Assert.Equal(DvbApi.Voltage18, tuning.Voltage);
        Assert.Equal(DvbApi.SysDvbS, Value(tuning, DvbApi.DeliverySystem));
        Assert.Equal(27_500_000u, Value(tuning, DvbApi.SymbolRate));
        Assert.Equal(3u, Value(tuning, DvbApi.InnerFec));
        Assert.Equal(DvbApi.Tune, tuning.Properties[^1].Command);
    }

    [Fact]
    public void A_high_band_transponder_uses_the_high_oscillator_with_the_tone()
    {
        var tuning = DvbTuning.For(Satellite(12188, Polarization.Vertical, DvbSystem.DvbS2), null, Universal);

        Assert.Equal((12_188_000u - 10_600_000u), Value(tuning, DvbApi.Frequency));
        Assert.Equal(DvbApi.ToneOn, tuning.Tone);
        Assert.Equal(DvbApi.Voltage13, tuning.Voltage);
        Assert.Equal(DvbApi.SysDvbS2, Value(tuning, DvbApi.DeliverySystem));
        Assert.Equal(9u, Value(tuning, DvbApi.Modulation));
    }

    [Fact]
    public void An_input_is_a_committed_diseqc_port_with_polarisation_and_band()
    {
        var tuning = DvbTuning.For(Satellite(12188, Polarization.Horizontal), 3, Universal);

        Assert.Equal(new byte[] { 0xE0, 0x10, 0x38, 0xFB, 0, 0, 4 }, Assert.Single(tuning.Commands));
    }

    [Fact]
    public void Cable_is_tuned_in_hertz_and_knows_no_lnb()
    {
        var tuning = DvbTuning.For(Cable, null, Universal);

        Assert.Equal(330_000_000u, Value(tuning, DvbApi.Frequency));
        Assert.Equal(DvbApi.SysDvbCAnnexA, Value(tuning, DvbApi.DeliverySystem));
        Assert.Equal(5u, Value(tuning, DvbApi.Modulation));
        Assert.Equal(6_900_000u, Value(tuning, DvbApi.SymbolRate));
        Assert.Null(tuning.Voltage);
        Assert.Empty(tuning.Commands);
    }

    [Fact]
    public void Dvb_t_is_tuned_in_hertz_with_what_is_unknown_left_to_the_card()
    {
        var tuning = DvbTuning.For(new TuningParameters { System = DvbSystem.DvbT, FrequencyKhz = 474_000, Bandwidth = "8" }, null, Universal);

        Assert.Equal(DvbApi.SysDvbT, Value(tuning, DvbApi.DeliverySystem));
        Assert.Equal(474_000_000u, Value(tuning, DvbApi.Frequency));
        Assert.Equal(8_000_000u, Value(tuning, DvbApi.BandwidthHz));
        Assert.Equal(DvbApi.TransmissionModeAuto, Value(tuning, DvbApi.TransmissionMode));
        Assert.Equal(DvbApi.GuardIntervalAuto, Value(tuning, DvbApi.GuardInterval));
        Assert.Equal(DvbApi.HierarchyAuto, Value(tuning, DvbApi.Hierarchy));
        Assert.Equal(DvbApi.Tune, tuning.Properties[^1].Command);
        Assert.Null(tuning.Voltage);
    }

    [Fact]
    public void Dvb_t2_names_its_physical_layer_pipe_and_takes_what_the_nit_said()
    {
        var tuning = DvbTuning.For(
            new TuningParameters { System = DvbSystem.DvbT2, FrequencyKhz = 546_000, Bandwidth = "1.712", Plp = 2, TransmissionMode = "32k", GuardInterval = "19128", Modulation = "256qam", Fec = "23" },
            null,
            Universal);

        Assert.Equal(DvbApi.SysDvbT2, Value(tuning, DvbApi.DeliverySystem));
        Assert.Equal(1_712_000u, Value(tuning, DvbApi.BandwidthHz));
        Assert.Equal(2u, Value(tuning, DvbApi.StreamId));
        Assert.Equal(6u, Value(tuning, DvbApi.TransmissionMode));
        Assert.Equal(6u, Value(tuning, DvbApi.GuardInterval));
        Assert.Equal(5u, Value(tuning, DvbApi.Modulation));
        Assert.Equal(2u, Value(tuning, DvbApi.CodeRateHigh));
    }

    [Fact]
    public void Unicable_i_asks_its_user_band_for_the_transponder()
    {
        var dish = new DishSettings { Unicable = DvbUnicable.En50494, UnicableSlot = 1, UnicableKhz = 1_210_000 };
        var tuning = DvbTuning.For(Satellite(11362, Polarization.Horizontal), 1, dish);

        Assert.Equal(new byte[] { 0xE0, 0x10, 0x5A, 0x29, 0x64, 0, 5 }, tuning.Unicable);
        Assert.Equal(1_212_000u, Value(tuning, DvbApi.Frequency));
        Assert.Equal(DvbApi.Voltage13, tuning.Voltage);
        Assert.Equal(DvbApi.ToneOff, tuning.Tone);
        Assert.Empty(tuning.Commands);
    }

    [Fact]
    public void Unicable_i_with_a_pin_has_a_command_of_its_own()
    {
        var dish = new DishSettings { Unicable = DvbUnicable.En50494, UnicableSlot = 1, UnicablePin = 42 };

        Assert.Equal(new byte[] { 0xE0, 0x10, 0x5C, 0x29, 0x64, 42, 6 }, DvbTuning.For(Satellite(11362, Polarization.Horizontal), 1, dish).Unicable);
    }

    [Fact]
    public void Unicable_ii_addresses_more_bands_and_positions_and_tunes_the_band_itself()
    {
        var dish = new DishSettings { Unicable = DvbUnicable.En50607, UnicableSlot = 3, UnicableKhz = 1_210_000 };
        var tuning = DvbTuning.For(Satellite(11362, Polarization.Horizontal), 2, dish);

        Assert.Equal(new byte[] { 0x70, 0x1D, 0xE8, 0x06, 0, 0, 4 }, tuning.Unicable);
        Assert.Equal(1_210_000u, Value(tuning, DvbApi.Frequency));
    }

    [Fact]
    public void A_transponder_out_of_reach_of_the_user_band_is_said_to_be()
    {
        var dish = new DishSettings { Unicable = DvbUnicable.En50607, LowKhz = 11_000_000 };

        Assert.Throws<TunerTransportException>(() => DvbTuning.For(Satellite(11050, Polarization.Horizontal), 1, dish));
    }

    [Fact]
    public void An_uncommitted_switch_and_a_tone_burst_take_their_own_commands()
    {
        var uncommitted = DvbTuning.For(Satellite(11362, Polarization.Horizontal), 3, new DishSettings { Switch = DvbSwitchKind.Uncommitted });

        Assert.Equal(new byte[] { 0xE0, 0x10, 0x39, 0xF2, 0, 0, 4 }, Assert.Single(uncommitted.Commands));

        var burst = DvbTuning.For(Satellite(11362, Polarization.Horizontal), 2, new DishSettings { Switch = DvbSwitchKind.ToneBurst });

        Assert.Equal(1, burst.Burst);
        Assert.Empty(burst.Commands);

        Assert.Empty(DvbTuning.For(Satellite(11362, Polarization.Horizontal), 1, new DishSettings { Switch = DvbSwitchKind.None }).Commands);
    }

    [Fact]
    public void A_motor_goes_to_a_stored_position_by_its_number()
    {
        var dish = new DishSettings { Rotor = DvbRotor.StoredPositions, RotorPositions = [192, 130, -8] };
        var tuning = DvbTuning.For(Satellite(11362, Polarization.Horizontal), 3, dish);

        Assert.Equal(new byte[] { 0xE0, 0x31, 0x6B, 3, 0, 0, 4 }, Assert.Single(tuning.Commands));
        Assert.Equal(-8, tuning.RotorTenths);
    }

    [Fact]
    public void A_motor_without_the_position_says_so()
    {
        var dish = new DishSettings { Rotor = DvbRotor.StoredPositions, RotorPositions = [192] };

        Assert.Throws<TunerTransportException>(() => DvbTuning.For(Satellite(11362, Polarization.Horizontal), 2, dish));
    }

    /// <summary>The expected tenths come from the published geometry, worked out apart from this code, with refraction.</summary>
    [Theory]
    [InlineData(52.5, 13.4, 19.2, 64)]
    [InlineData(52.5, 13.4, 13.0, -4)]
    [InlineData(52.5, 13.4, -0.8, -156)]
    [InlineData(52.5, 13.4, 28.2, 163)]
    [InlineData(48.1, 11.6, -30, -457)]
    [InlineData(47.4, 8.5, 45, 403)]
    [InlineData(-33.9, 151.2, 156, -55)]
    [InlineData(-33.9, 151.2, 140, 128)]
    [InlineData(40.4, -3.7, -30, -296)]
    [InlineData(60.2, 24.9, 0.8, -260)]
    public void Usals_angles_match_the_published_geometry(double latitude, double longitude, double satellite, int tenths) =>
        Assert.InRange(DvbTuning.UsalsAngle(latitude, longitude, satellite) * 10, tenths - 3, tenths + 3);

    [Fact]
    public void A_usals_command_carries_direction_degrees_and_coded_tenths()
    {
        Assert.Equal(0xE066, DvbTuning.UsalsValue(6.4));
        Assert.Equal(0xD0FA, DvbTuning.UsalsValue(-15.6));

        var dish = new DishSettings { Rotor = DvbRotor.Usals, RotorPositions = [192], SiteLatitude = 52.5, SiteLongitude = 13.4 };
        var command = Assert.Single(DvbTuning.For(Satellite(11362, Polarization.Horizontal), 1, dish).Commands);

        Assert.Equal(new byte[] { 0xE0, 0x31, 0x6E }, command[..3]);
        Assert.Equal(5, command[6]);
    }

    [Fact]
    public void A_motor_is_given_the_time_for_its_way()
    {
        Assert.Equal(TimeSpan.FromMilliseconds((6.2 * 700) + 1000), DvbTuning.RotorTravel(130, 192, 700));
        Assert.Equal(TimeSpan.FromMilliseconds((30 * 700) + 1000), DvbTuning.RotorTravel(null, 192, 700));
    }

    [Fact]
    public void Positions_are_read_as_they_are_written()
    {
        Assert.Equal([192, -8, 130], DishSettings.ParsePositions("19.2E, 0.8W, 13e, nonsense"));
    }

    [Fact]
    public void Inputs_follow_what_hangs_on_the_cable()
    {
        Assert.Equal(4, new DishSettings().Inputs);
        Assert.Equal(1, new DishSettings { Switch = DvbSwitchKind.None }.Inputs);
        Assert.Equal(2, new DishSettings { Switch = DvbSwitchKind.ToneBurst }.Inputs);
        Assert.Equal(2, new DishSettings { Unicable = DvbUnicable.En50494 }.Inputs);
        Assert.Equal(3, new DishSettings { Rotor = DvbRotor.Usals, RotorPositions = [192, 130, 235] }.Inputs);
    }

    [Fact]
    public async Task A_unicable_command_goes_at_18_volts_and_the_signal_comes_at_13()
    {
        var kernel = new FakeKernel();
        var dish = new DishSettings { Unicable = DvbUnicable.En50494 };

        await using var transport = Transport(kernel, new TuneRequest(Satellite(11362, Polarization.Horizontal), 1), dish);

        await transport.StartAsync([0], CancellationToken.None);

        var relevant = kernel.Calls.Where(call => call.Request == DvbApi.FeSetVoltage || call.Request == DvbApi.FeSendDiseqc).ToList();

        Assert.Equal(DvbApi.FeSetVoltage, relevant[0].Request);
        Assert.Equal(DvbApi.Voltage18, relevant[0].Value);
        Assert.Equal(DvbApi.FeSendDiseqc, relevant[1].Request);
        Assert.Equal(DvbApi.Voltage13, relevant[2].Value);
    }

    [Fact]
    public void A_terrestrial_card_counts_as_one_tuner_too()
    {
        var device = DeviceMapping.Card(new DvbCardSettings(), new DvbCard(0, 0, "Stick", Satellite: false, Cable: true, Terrestrial: true));

        Assert.Equal(1, device.Capacity(DeliverySystem.Terrestrial));
        Assert.Equal(1, device.TotalCapacity);
    }

    [Fact]
    public void Measurements_keep_their_units_and_what_is_not_measured_stays_unknown()
    {
        var measured = DvbTuning.Signal(DvbApi.StatusSignal | DvbApi.StatusLock, (DvbApi.ScaleDecibel, -55_000), (DvbApi.ScaleDecibel, 12_500), 5, 1000);

        Assert.True(measured.Locked);
        Assert.True(measured.Measured);
        Assert.Equal(128, measured.Level);
        Assert.Equal(12.5, measured.SnrDb);
        Assert.Equal(0.005, measured.BitErrorRate);
        Assert.Equal("-55.0 dBm", measured.LevelRaw);

        var bare = DvbTuning.Signal(DvbApi.StatusSignal | DvbApi.StatusLock, null, null, null, null);

        Assert.True(bare.Locked);
        Assert.False(bare.Measured);
        Assert.Null(bare.SnrDb);
        Assert.Null(bare.BitErrorRate);
    }

    [Fact]
    public async Task A_stream_tunes_filters_and_delivers()
    {
        var kernel = new FakeKernel();

        await using var transport = Transport(kernel, new TuneRequest(Cable, null));

        await transport.StartAsync([0, 100, 101], CancellationToken.None);

        Assert.Contains(kernel.Calls, call => call.Request == DvbApi.DmxSetBufferSize);
        Assert.Single(kernel.Filters, 0);
        Assert.Equal([100, 101], kernel.Added);

        var tune = kernel.PropertyWrites[^1];

        Assert.Equal(DvbApi.DeliverySystem, BinaryPrimitives.ReadUInt32LittleEndian(tune));

        kernel.Deliver(new byte[188]);

        var buffer = new byte[188];

        Assert.Equal(188, await transport.ReadAsync(buffer, CancellationToken.None));

        await transport.SetPidsAsync([0, 100, 200], CancellationToken.None);

        Assert.Contains(200, kernel.Added);
        Assert.Equal([101], kernel.Removed);
        Assert.Equal(StreamPath.LocalCard, transport.Status.Path);
    }

    [Fact]
    public async Task The_lnb_is_set_before_the_demodulator()
    {
        var kernel = new FakeKernel();

        await using var transport = Transport(kernel, new TuneRequest(Satellite(11362, Polarization.Horizontal), 1));

        await transport.StartAsync([0], CancellationToken.None);

        var order = kernel.Calls.Select(call => call.Request).ToList();

        Assert.True(order.IndexOf(DvbApi.FeSetVoltage) < order.IndexOf(DvbApi.FeSendDiseqc));
        Assert.True(order.IndexOf(DvbApi.FeSendDiseqc) < order.LastIndexOf(DvbApi.FeSetTone));
        Assert.True(order.LastIndexOf(DvbApi.FeSetTone) < order.IndexOf(DvbApi.FeSetProperty));
    }

    [Fact]
    public async Task A_card_in_use_says_so()
    {
        var kernel = new FakeKernel { OpenResult = -16 };

        await using var transport = Transport(kernel, new TuneRequest(Cable, null));

        var error = await Assert.ThrowsAsync<TunerTransportException>(() => transport.StartAsync([0], CancellationToken.None));

        Assert.Contains("in use by another program", error.Message, StringComparison.Ordinal);
        Assert.True(error.Unreachable);
    }

    [Fact]
    public async Task An_overflow_is_counted_and_reading_goes_on()
    {
        var kernel = new FakeKernel();

        await using var transport = Transport(kernel, new TuneRequest(Cable, null));

        await transport.StartAsync([0], CancellationToken.None);

        kernel.Fail(-75);
        kernel.Deliver(new byte[188]);

        Assert.Equal(188, await transport.ReadAsync(new byte[188], CancellationToken.None));
        Assert.Equal(1, transport.Status.DatagramsLost);
    }

    [Fact]
    public void A_frontend_says_what_it_receives()
    {
        var kernel = new FakeKernel { FrontendName = "Sundtek", Systems = [(byte)DvbApi.SysDvbCAnnexA, 16] };

        var card = DvbCards.Describe(kernel, "/dev/dvb/adapter1/frontend0", 1, 0);

        Assert.Equal(new DvbCard(1, 0, "Sundtek", Satellite: false, Cable: true, Terrestrial: true), card);
    }

    [Fact]
    public void A_card_is_one_tuner_however_many_systems_it_knows()
    {
        var device = DeviceMapping.Card(new DvbCardSettings { Adapter = 2 }, new DvbCard(2, 0, "TBS", Satellite: true, Cable: true));

        Assert.Equal("dvb:adapter2.0", device.Id);
        Assert.Equal(1, device.Capacity(DeliverySystem.Satellite));
        Assert.Equal(1, device.Capacity(DeliverySystem.Cable));
        Assert.Equal(1, device.TotalCapacity);
        Assert.Null(device.Error);
    }

    [Fact]
    public void A_card_that_cannot_be_read_says_what_to_check()
    {
        var device = DeviceMapping.Card(new DvbCardSettings(), null);

        Assert.NotNull(device.Error);
        Assert.Equal(0, device.Capacity(DeliverySystem.Network));
    }

    [Fact]
    public void A_fritzbox_is_recognised_from_its_description()
    {
        var description = SatIpDeviceDescription.Parse(
            """
            <root xmlns="urn:schemas-upnp-org:device-1-0"><device>
              <friendlyName>FRITZ!Box 6591 Cable</friendlyName>
              <manufacturer>AVM Berlin</manufacturer>
              <modelName>FRITZ!Box 6591 Cable</modelName>
              <satip:X_SATIPCAP xmlns:satip="urn:ses-com:satip">DVBC-4</satip:X_SATIPCAP>
            </device></root>
            """)!;

        var device = DeviceMapping.SatIp("192.0.2.1", null, new DiscoveredSatIpServer("192.0.2.1", description), null);

        Assert.True(device.IsFritzBox);
        Assert.Equal(4, device.Capacity(DeliverySystem.Cable));
        Assert.Equal(0, device.Capacity(DeliverySystem.Satellite));
    }

    [Fact]
    public void A_fritzbox_gets_the_whole_pid_list_and_others_the_changes()
    {
        Assert.Equal("pids=0,100", SatIpQuery.PidChange([0, 100], [100], [200], fullList: true));
        Assert.Equal("addpids=100&delpids=200", SatIpQuery.PidChange([0, 100], [100], [200], fullList: false));
    }

    [Fact]
    public void Cable_goes_out_as_sat_ip_writes_it()
    {
        Assert.Equal("freq=330&msys=dvbc&mtype=256qam&sr=6900", SatIpQuery.Tuning(Cable, null));
    }

    private static LinuxDvbTransport Transport(FakeKernel kernel, TuneRequest request, DishSettings? dish = null)
    {
        var card = new LocalDvbCard(0, 0, dish ?? Universal);
        var device = new TunerDevice { Id = "dvb:adapter0.0", Name = "Card", Host = "adapter0.0", Card = card };

        return new LinuxDvbTransport(device, card, request, kernel, NullLogger.Instance);
    }

    private sealed class FakeKernel : IDvbKernel
    {
        private readonly Queue<object> _reads = new();
        private int _next = 10;

        public int? OpenResult { get; init; }

        public string FrontendName { get; init; } = "Fake";

        public byte[] Systems { get; init; } = [];

        public List<(ulong Request, long Value)> Calls { get; } = [];

        public List<byte[]> PropertyWrites { get; } = [];

        public List<int> Filters { get; } = [];

        public List<int> Added { get; } = [];

        public List<int> Removed { get; } = [];

        public void Deliver(byte[] data)
        {
            lock (_reads)
            {
                _reads.Enqueue(data);
            }
        }

        public void Fail(int errno)
        {
            lock (_reads)
            {
                _reads.Enqueue(errno);
            }
        }

        public int Open(string path, bool writable) => OpenResult ?? _next++;

        public void Close(int descriptor)
        {
        }

        public int Control(int descriptor, ulong request, nint value)
        {
            lock (Calls)
            {
                Calls.Add((request, value));
            }

            return 0;
        }

        public int Control(int descriptor, ulong request, byte[] data)
        {
            lock (Calls)
            {
                Calls.Add((request, 0));

                if (request == DvbApi.DmxSetPesFilter)
                {
                    Filters.Add(BitConverter.ToUInt16(data));
                }
                else if (request == DvbApi.DmxAddPid)
                {
                    Added.Add(BitConverter.ToUInt16(data));
                }
                else if (request == DvbApi.DmxRemovePid)
                {
                    Removed.Add(BitConverter.ToUInt16(data));
                }
                else if (request == DvbApi.FeGetInfo)
                {
                    System.Text.Encoding.UTF8.GetBytes(FrontendName).CopyTo(data, 0);
                }
                else if (request == DvbApi.FeReadStatus)
                {
                    data[0] = (byte)DvbApi.StatusLock;
                }
            }

            return 0;
        }

        public int Properties(int descriptor, ulong request, byte[] entries)
        {
            lock (Calls)
            {
                Calls.Add((request, 0));

                if (request == DvbApi.FeSetProperty)
                {
                    PropertyWrites.Add(entries);
                }
                else if (BinaryPrimitives.ReadUInt32LittleEndian(entries) == DvbApi.EnumerateSystems)
                {
                    Systems.CopyTo(entries, 16);
                    BinaryPrimitives.WriteUInt32LittleEndian(entries.AsSpan(48), (uint)Systems.Length);
                }
            }

            return 0;
        }

        public int Read(int descriptor, Span<byte> buffer)
        {
            lock (_reads)
            {
                if (!_reads.TryDequeue(out var next))
                {
                    return -11;
                }

                if (next is int errno)
                {
                    return errno;
                }

                var data = (byte[])next;

                data.CopyTo(buffer);

                return data.Length;
            }
        }

        public bool WaitReadable(int descriptor, int milliseconds)
        {
            lock (_reads)
            {
                if (_reads.Count > 0)
                {
                    return true;
                }
            }

            Thread.Sleep(1);

            return false;
        }
    }
}
