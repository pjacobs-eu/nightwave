using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Nightwave.Core.Tuning;

namespace Nightwave.Tuners.Dvb.Devices;

/// <summary>
/// What a SAT>IP server says about itself in its UPnP device description (SAT>IP 1.2.2, 3.4):
/// a name, a unique id, and <c>X_SATIPCAP</c> - which systems it tunes, with how many tuners.
/// </summary>
internal sealed record SatIpDeviceDescription
{
    public required string FriendlyName { get; init; }

    public string? UniqueDeviceName { get; init; }

    public string? ModelName { get; init; }

    public string? Manufacturer { get; init; }

    public required IReadOnlyDictionary<DeliverySystem, int> Tuners { get; init; }

    /// <summary><c>X_SATIPCAP</c> as the server wrote it, for example <c>DVBS2-8</c>.</summary>
    public string? Capabilities { get; init; }

    /// <summary>
    /// Reads a device description. Element names are matched without their namespace: servers
    /// disagree about how to declare the <c>satip</c> one, and the names are unambiguous anyway.
    /// </summary>
    public static SatIpDeviceDescription? Parse(string xml)
    {
        XDocument document;

        try
        {
            document = XDocument.Parse(xml, LoadOptions.None);
        }
        catch (XmlException)
        {
            return null;
        }

        var device = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "device");
        if (device is null)
        {
            return null;
        }

        string? Value(string name) =>
            device.Descendants().FirstOrDefault(element => element.Name.LocalName == name)?.Value.Trim();

        return new SatIpDeviceDescription
        {
            FriendlyName = Value("friendlyName") is { Length: > 0 } name ? name : "SAT>IP server",
            UniqueDeviceName = Value("UDN"),
            ModelName = Value("modelName"),
            Manufacturer = Value("manufacturer"),
            Tuners = ParseCapabilities(Value("X_SATIPCAP")),
            Capabilities = Value("X_SATIPCAP"),
        };
    }

    /// <summary>
    /// Reads <c>X_SATIPCAP</c>, for example <c>DVBS2-4,DVBC-2</c>: four satellite tuners and two
    /// cable tuners. DVB-S2 tuners tune DVB-S as well, and T2 and C2 the older systems, so each
    /// entry counts for its whole kind of signal.
    /// </summary>
    public static IReadOnlyDictionary<DeliverySystem, int> ParseCapabilities(string? capabilities)
    {
        var tuners = new Dictionary<DeliverySystem, int>();

        if (string.IsNullOrWhiteSpace(capabilities))
        {
            return tuners;
        }

        foreach (var entry in capabilities.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = entry.LastIndexOf('-');
            if (dash <= 0 || !int.TryParse(entry[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) || count <= 0)
            {
                continue;
            }

            DeliverySystem? delivery = entry[..dash].ToUpperInvariant() switch
            {
                "DVBS" or "DVBS2" => DeliverySystem.Satellite,
                "DVBC" or "DVBC2" => DeliverySystem.Cable,
                "DVBT" or "DVBT2" => DeliverySystem.Terrestrial,
                _ => null,
            };

            if (delivery is { } known)
            {
                tuners[known] = tuners.GetValueOrDefault(known) + count;
            }
        }

        return tuners;
    }
}
