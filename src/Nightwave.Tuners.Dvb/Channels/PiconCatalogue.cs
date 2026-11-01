using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using Nightwave.Core.Tuning;

namespace Nightwave.Tuners.Dvb.Channels;

/// <summary>
/// The channel logos: the picons project's service reference picons
/// (https://github.com/picons/picons) for the positions Nightwave scans, built into
/// <c>picons.zip</c> by <c>packaging/picons.sh</c>.
/// </summary>
/// <remarks>
/// A picon is found by the numbers that identify a DVB service, the way an Enigma2 receiver
/// names it: <c>SID_TSID_ONID_NAMESPACE</c> in upper case hexadecimal, the namespace being the
/// orbital position in tenths of a degree shifted left by 16, west counted back from 360.
/// Channel names play no part, so a renamed channel keeps its logo and two channels called the
/// same keep theirs apart. Only satellite services have picons here; cable and terrestrial
/// namespaces depend on the network and are not in the archive.
/// <para>
/// Without the archive there are no logos and says so once in the log; everything
/// else works as before.
/// </para>
/// </remarks>
internal sealed class PiconCatalogue : IDisposable
{
    private readonly ZipArchive? _archive;
    private readonly Dictionary<string, string> _index;
    private readonly ConcurrentDictionary<string, ChannelLogo?> _pictures = new(StringComparer.Ordinal);
    private readonly Lock _read = new();

    public PiconCatalogue(Stream? archive)
    {
        _index = new Dictionary<string, string>(StringComparer.Ordinal);

        if (archive is null)
        {
            return;
        }

        _archive = new ZipArchive(archive, ZipArchiveMode.Read);

        using var reader = new StreamReader(
            _archive.GetEntry("index.txt")?.Open()
                ?? throw new InvalidDataException("The picon archive has no index.txt; rebuild it with packaging/picons.sh."));

        while (reader.ReadLine() is { } line)
        {
            var separator = line.IndexOf('=', StringComparison.Ordinal);

            if (separator > 0)
            {
                _index[line[..separator]] = line[(separator + 1)..];
            }
        }
    }

    /// <summary>
    /// The archive at the first of these paths that exists, or none. Kept as a file beside the
    /// program rather than compiled in: the logos are the picons project's, under GPL-3.0, and
    /// stay data that can be replaced or left out.
    /// </summary>
    public static PiconCatalogue FromFile(params string[] paths) =>
        new(paths.FirstOrDefault(File.Exists) is { } path ? File.OpenRead(path) : null);

    /// <summary>How many services have a picon.</summary>
    public int Count => _index.Count;

    public bool Has(string channelId) => Key(channelId) is { } key && _index.ContainsKey(key);

    /// <summary>
    /// The picon drawn for the background, or the other style when a broadcaster's logo only
    /// exists in one.
    /// </summary>
    public ChannelLogo? Find(string channelId, LogoBackground background)
    {
        if (Key(channelId) is not { } key || !_index.TryGetValue(key, out var name))
        {
            return null;
        }

        // The picons' "light" logos are the ones drawn to stand out on a dark background.
        var (wanted, other) = background == LogoBackground.Dark ? ("light", "dark") : ("dark", "light");

        return Picture($"{wanted}/{name}.png") ?? Picture($"{other}/{name}.png");
    }

    /// <summary>
    /// The Enigma2 service reference key of a channel id as <see cref="StoredChannel.ServiceKey"/>
    /// writes it, for example <c>19.2E:1.1019.10301</c>; null for a cable or terrestrial channel.
    /// </summary>
    public static string? Key(string channelId)
    {
        var separator = channelId.IndexOf(':', StringComparison.Ordinal);

        if (separator <= 0 || !OrbitalPosition.TryParse(channelId[..separator], out var position))
        {
            return null;
        }

        var ids = channelId[(separator + 1)..].Split('.');

        if (ids.Length != 3
            || !int.TryParse(ids[0], NumberStyles.None, CultureInfo.InvariantCulture, out var originalNetworkId)
            || !int.TryParse(ids[1], NumberStyles.None, CultureInfo.InvariantCulture, out var transportStreamId)
            || !int.TryParse(ids[2], NumberStyles.None, CultureInfo.InvariantCulture, out var serviceId))
        {
            return null;
        }

        var tenths = position.Tenths >= 0 ? position.Tenths : 3600 + position.Tenths;
        var ns = (long)tenths << 16;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{serviceId:X}_{transportStreamId:X}_{originalNetworkId:X}_{ns:X}");
    }

    private ChannelLogo? Picture(string entryName) =>
        _pictures.GetOrAdd(entryName, name =>
        {
            // One reader at a time: a ZipArchive shares one stream between its entries.
            lock (_read)
            {
                if (_archive?.GetEntry(name) is not { } entry)
                {
                    return null;
                }

                using var content = entry.Open();
                using var copy = new MemoryStream((int)entry.Length);
                content.CopyTo(copy);

                // The style is part of the tag: both styles of a logo share a file name.
                var tag = string.Create(CultureInfo.InvariantCulture, $"{name[0]}{entry.Crc32:x8}");

                return new ChannelLogo(copy.ToArray(), "image/png", tag);
            }
        });

    public void Dispose() => _archive?.Dispose();
}
