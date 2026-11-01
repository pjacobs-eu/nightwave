using System.Globalization;
using Nightwave.Core.Storage;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nightwave.Core.Tuning;

namespace Nightwave.Tuners.Dvb.Channels;

/// <summary>
/// The channels scans found: read once, then answered from memory. A scan's outcome goes to the
/// database in one transaction, so a crash in the middle leaves the previous list.
/// </summary>
internal sealed class ChannelStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly NightwaveDatabase _database;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private Document? _document;

    public ChannelStore(NightwaveDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
        _database.Cleared += (_, everything) =>
        {
            if (everything)
            {
                _document = null;
            }
        };
    }

    public void Dispose() => _mutex.Dispose();

    public async Task<IReadOnlyList<StoredChannel>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);

        return document.Channels;
    }

    public async Task<StoredChannel?> FindAsync(string channelId, CancellationToken cancellationToken)
    {
        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);

        return document.Channels.FirstOrDefault(channel => string.Equals(channel.Id, channelId, StringComparison.Ordinal));
    }

    /// <summary>How many channels a target holds, and when it was last scanned to the end.</summary>
    public async Task<(int Channels, DateTimeOffset? LastScannedAt)> DescribeTargetAsync(string targetId, CancellationToken cancellationToken)
    {
        var document = await LoadAsync(cancellationToken).ConfigureAwait(false);

        return (
            document.Channels.Count(channel => channel.FoundBy.Contains(targetId, StringComparer.Ordinal)),
            document.Targets.TryGetValue(targetId, out var scannedAt) ? scannedAt : null);
    }

    /// <summary>
    /// Takes what a scan of one target found. A replacing scan takes the target off every
    /// channel it did not find again, and a channel no target found any more is gone; a merging
    /// scan only adds and updates. Returns how many channels went.
    /// </summary>
    public async Task<int> ApplyScanAsync(
        string targetId,
        IReadOnlyList<StoredChannel> found,
        ScanMode mode,
        DateTimeOffset? completedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(found);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var document = LoadUnlocked();
            var channels = document.Channels.ToDictionary(channel => channel.Id, StringComparer.Ordinal);
            var foundIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var channel in found)
            {
                foundIds.Add(channel.Id);

                var foundBy = channels.TryGetValue(channel.Id, out var existing)
                    ? existing.FoundBy.Append(targetId).Distinct(StringComparer.Ordinal).ToList()
                    : [targetId];

                channels[channel.Id] = channel with { FoundBy = foundBy };
            }

            var removed = 0;

            if (mode == ScanMode.Replace)
            {
                foreach (var channel in channels.Values.ToList())
                {
                    if (foundIds.Contains(channel.Id) || !channel.FoundBy.Contains(targetId, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    var remaining = channel.FoundBy.Where(target => !string.Equals(target, targetId, StringComparison.Ordinal)).ToList();

                    if (remaining.Count == 0)
                    {
                        channels.Remove(channel.Id);
                        removed++;
                    }
                    else
                    {
                        channels[channel.Id] = channel with { FoundBy = remaining };
                    }
                }
            }

            var targets = new Dictionary<string, DateTimeOffset>(document.Targets, StringComparer.Ordinal);
            if (completedAt is { } at)
            {
                targets[targetId] = at;
            }

            var updated = new Document
            {
                Channels = [.. channels.Values.OrderBy(channel => channel.Name, StringComparer.CurrentCultureIgnoreCase)],
                Targets = targets,
            };

            Save(updated);
            _document = updated;

            return removed;
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<Document> LoadAsync(CancellationToken cancellationToken)
    {
        if (_document is { } cached)
        {
            return cached;
        }

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return LoadUnlocked();
        }
        finally
        {
            _mutex.Release();
        }
    }

    private Document LoadUnlocked()
    {
        if (_document is { } cached)
        {
            return cached;
        }

        using var connection = _database.Open();
        var channels = new List<StoredChannel>();
        var targets = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT data FROM satip_channels ORDER BY name COLLATE NOCASE;";

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                channels.Add(JsonSerializer.Deserialize<StoredChannel>(reader.GetString(0), Json)!);
            }
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, scanned_at FROM satip_scan_targets;";

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                targets[reader.GetString(0)] = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
            }
        }

        _document = new Document { Channels = channels, Targets = targets };

        return _document;
    }

    private void Save(Document document)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM satip_channels; DELETE FROM satip_scan_targets;";
            clear.ExecuteNonQuery();
        }

        foreach (var channel in document.Channels)
        {
            using var insert = connection.CreateCommand();

            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO satip_channels (id, name, data) VALUES ($id, $name, $data);";
            insert.Parameters.AddWithValue("$id", channel.Id);
            insert.Parameters.AddWithValue("$name", channel.Name);
            insert.Parameters.AddWithValue("$data", JsonSerializer.Serialize(channel, Json));
            insert.ExecuteNonQuery();
        }

        foreach (var (id, scannedAt) in document.Targets)
        {
            using var insert = connection.CreateCommand();

            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO satip_scan_targets (id, scanned_at) VALUES ($id, $at);";
            insert.Parameters.AddWithValue("$id", id);
            insert.Parameters.AddWithValue("$at", scannedAt.ToString("O", CultureInfo.InvariantCulture));
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>The channels, and when each target was last scanned to the end.</summary>
    private sealed record Document
    {
        public IReadOnlyList<StoredChannel> Channels { get; init; } = [];

        public Dictionary<string, DateTimeOffset> Targets { get; init; } = new(StringComparer.Ordinal);
    }
}
