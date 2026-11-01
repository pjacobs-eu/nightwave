using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb;
using Nightwave.Tuners.Dvb.Devices;

namespace Nightwave.Tuners.Dvb.Tests;

/// <summary>
/// A SAT>IP server that does not answer the SSDP search, written down by where its device
/// description is.
/// </summary>
public sealed class DeviceDescriptionUrlTests
{
    private static readonly Uri Description = new("http://192.168.1.2/rootDescr");

    /// <summary>
    /// The description a Kathrein EXIP-418 serves at <c>/rootDescr</c>, as it came off the wire: one
    /// line, the SAT>IP capability in its own namespace. This server does not answer the SSDP
    /// search, which is why it has to be written down by this URL.
    /// </summary>
    private const string KathreinExip418 =
        """<?xml version="1.0" encoding="UTF-8"?><root xmlns="urn:schemas-upnp-org:device-1-0" configId="16123804"><specVersion><major>1</major><minor>1</minor></specVersion><device><deviceType>urn:ses-com:device:SatIPServer:1</deviceType><friendlyName>KATHREIN SatIP Server 23:BD:33</friendlyName><manufacturer>KATHREIN</manufacturer><manufacturerURL>http://www.kathrein.com</manufacturerURL><modelDescription>EXIP-418 UPnP Device</modelDescription><modelName>EXIP-418</modelName><modelNumber>1.0</modelNumber><modelURL>http://www.kathrein.com</modelURL><UDN>uuid:00000000-0000-1000-ad7d-00d05523bd33</UDN><UPC>123456789</UPC><iconList><icon><mimetype>image/png</mimetype><width>120</width><height>120</height><depth>24</depth><url>/upnp/SAT_IP_Icon_120x120.png</url></icon></iconList><presentationURL>/</presentationURL><satip:X_SATIPCAP xmlns:satip="urn:ses-com:satip">DVBS2-8</satip:X_SATIPCAP></device></root>""";

    [Fact]
    public void A_kathrein_description_is_read()
    {
        var description = SatIpDeviceDescription.Parse(KathreinExip418);

        Assert.NotNull(description);
        Assert.Equal("KATHREIN SatIP Server 23:BD:33", description.FriendlyName);
        Assert.Equal(8, description.Tuners[DeliverySystem.Satellite]);
    }

    [Fact]
    public async Task A_description_url_alone_is_enough_and_its_tuners_are_known()
    {
        var discovery = new FakeDeviceDiscovery();
        discovery.Descriptions[Description] = new SatIpDeviceDescription
        {
            FriendlyName = "Receiver",
            Tuners = SatIpDeviceDescription.ParseCapabilities("DVBS2-2"),
        };

        var device = Assert.Single(await DevicesAsync(discovery, new SatIpDeviceSettings { DescriptionUrl = Description.ToString() }));

        Assert.Equal("192.168.1.2", device.Host);
        Assert.Equal("Receiver", device.Name);
        Assert.True(device.TunersKnown);
        Assert.Equal(2, device.Tuners[DeliverySystem.Satellite]);
        Assert.Null(device.Error);
    }

    /// <summary>The port in the description URL is the web server's, not the RTSP server's.</summary>
    [Fact]
    public async Task The_port_of_the_description_url_is_not_taken_for_rtsp()
    {
        var discovery = new FakeDeviceDiscovery();
        var description = new Uri("http://192.168.1.2:8080/desc.xml");
        discovery.Descriptions[description] = new SatIpDeviceDescription
        {
            FriendlyName = "Receiver",
            Tuners = SatIpDeviceDescription.ParseCapabilities("DVBS2-1"),
        };

        var device = Assert.Single(await DevicesAsync(discovery, new SatIpDeviceSettings { DescriptionUrl = description.ToString() }));

        Assert.Equal(554, device.RtspPort);
    }

    [Fact]
    public async Task An_address_with_a_port_keeps_its_rtsp_port_beside_a_description_url()
    {
        var discovery = new FakeDeviceDiscovery();
        discovery.Descriptions[Description] = new SatIpDeviceDescription
        {
            FriendlyName = "Receiver",
            Tuners = SatIpDeviceDescription.ParseCapabilities("DVBS2-2"),
        };

        var device = Assert.Single(await DevicesAsync(
            discovery,
            new SatIpDeviceSettings { Address = "192.168.1.2:8554", DescriptionUrl = Description.ToString() }));

        Assert.Equal(8554, device.RtspPort);
        Assert.True(device.TunersKnown);
    }

    [Fact]
    public async Task An_unreadable_description_says_which_url_to_check()
    {
        var device = Assert.Single(await DevicesAsync(
            new FakeDeviceDiscovery(),
            new SatIpDeviceSettings { DescriptionUrl = Description.ToString(), MaxTuners = 2 }));

        Assert.False(device.TunersKnown);
        Assert.Contains(Description.ToString(), device.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_entry_with_neither_address_nor_description_is_ignored()
    {
        Assert.Empty(await DevicesAsync(new FakeDeviceDiscovery(), new SatIpDeviceSettings { DescriptionUrl = "not a url" }));
    }

    private static async Task<IReadOnlyList<TunerDevice>> DevicesAsync(FakeDeviceDiscovery discovery, SatIpDeviceSettings entry)
    {
        var settings = new DvbSettingsProvider(new DvbSettings { AutoDiscover = false, Devices = [entry] });
        using var devices = new DeviceDirectory(settings, discovery, TimeProvider.System);

        return await devices.GetDevicesAsync(refresh: true, CancellationToken.None);
    }
}
