using System.Globalization;
using Nightwave.Core.Tuning;

namespace Nightwave.Tuners.Dvb.LinuxDvb;

/// <summary>A frontend of a card in this machine, as the tuner settings refer to it.</summary>
internal sealed record LocalDvbCard(int Adapter, int Frontend, DishSettings Dish)
{
    private string Directory => string.Create(CultureInfo.InvariantCulture, $"{DvbCards.Root}/adapter{Adapter}");

    public string FrontendPath => string.Create(CultureInfo.InvariantCulture, $"{Directory}/frontend{Frontend}");

    public string DemuxPath => string.Create(CultureInfo.InvariantCulture, $"{Directory}/demux{Frontend}");

    public string DvrPath => string.Create(CultureInfo.InvariantCulture, $"{Directory}/dvr{Frontend}");
}

/// <summary>A frontend found in this machine.</summary>
/// <param name="Adapter">The number of <c>/dev/dvb/adapterN</c>.</param>
/// <param name="Frontend">The number of <c>frontendN</c> on it.</param>
/// <param name="Name">What its driver calls it.</param>
/// <param name="Satellite">Whether it receives DVB-S or DVB-S2.</param>
/// <param name="Cable">Whether it receives DVB-C.</param>
/// <param name="Terrestrial">Whether it receives DVB-T or DVB-T2.</param>
public sealed record DvbCard(int Adapter, int Frontend, string Name, bool Satellite, bool Cable, bool Terrestrial = false);

/// <summary>The DVB cards of this machine.</summary>
public static class DvbCards
{
    public const string Root = "/dev/dvb";

    /// <summary>Every frontend found. Empty on a machine without cards, and on anything but Linux.</summary>
    public static IReadOnlyList<DvbCard> Find() =>
        LinuxDvbKernel.Unsupported is null ? Find(LinuxDvbKernel.Instance, Root) : [];

    internal static IReadOnlyList<DvbCard> Find(IDvbKernel kernel, string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var cards = new List<DvbCard>();

        foreach (var adapter in Directory.EnumerateDirectories(root, "adapter*"))
        {
            foreach (var frontend in Directory.EnumerateFileSystemEntries(adapter, "frontend*"))
            {
                if (Number(adapter, "adapter") is { } adapterNumber
                    && Number(frontend, "frontend") is { } frontendNumber
                    && Describe(kernel, frontend, adapterNumber, frontendNumber) is { } card)
                {
                    cards.Add(card);
                }
            }
        }

        return [.. cards.OrderBy(card => card.Adapter).ThenBy(card => card.Frontend)];
    }

    /// <summary>What a frontend receives, or null where it cannot be asked.</summary>
    internal static DvbCard? Describe(IDvbKernel kernel, string path, int adapter, int frontend)
    {
        // Read only: a frontend somebody else is tuning can still be asked this.
        var descriptor = kernel.Open(path, writable: false);

        if (descriptor < 0)
        {
            return null;
        }

        try
        {
            var info = new byte[(int)DvbApi.FrontendInfoSize];
            var systems = DvbApi.Properties([(DvbApi.EnumerateSystems, 0)]);

            if (kernel.Control(descriptor, DvbApi.FeGetInfo, info) < 0 || kernel.Properties(descriptor, DvbApi.FeGetProperty, systems) < 0)
            {
                return null;
            }

            var delivery = DvbApi.Systems(systems);

            return new DvbCard(
                adapter,
                frontend,
                DvbApi.FrontendName(info),
                delivery.Contains((byte)DvbApi.SysDvbS) || delivery.Contains((byte)DvbApi.SysDvbS2),
                delivery.Contains((byte)DvbApi.SysDvbCAnnexA) || delivery.Contains((byte)DvbApi.SysDvbCAnnexC),
                delivery.Contains((byte)DvbApi.SysDvbT) || delivery.Contains((byte)DvbApi.SysDvbT2));
        }
        finally
        {
            kernel.Close(descriptor);
        }
    }

    internal static IReadOnlyDictionary<DeliverySystem, int> Tuners(DvbCard card)
    {
        var tuners = new Dictionary<DeliverySystem, int>();

        if (card.Satellite)
        {
            tuners[DeliverySystem.Satellite] = 1;
        }

        if (card.Cable)
        {
            tuners[DeliverySystem.Cable] = 1;
        }

        if (card.Terrestrial)
        {
            tuners[DeliverySystem.Terrestrial] = 1;
        }

        return tuners;
    }

    private static int? Number(string path, string prefix) =>
        int.TryParse(Path.GetFileName(path).AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
}
