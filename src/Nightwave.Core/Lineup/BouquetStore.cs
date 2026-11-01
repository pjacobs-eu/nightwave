using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Nightwave.Core.Storage;

namespace Nightwave.Core.Lineup;

/// <summary>The bouquets: read once, then answered from memory and written through to the database.</summary>
public sealed class BouquetStore : IDisposable
{
    public const int MaxNameLength = 100;

    private readonly NightwaveDatabase _database;
    private readonly SemaphoreSlim _write = new(1, 1);
    private volatile List<Bouquet> _bouquets;

    public BouquetStore(NightwaveDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
        _bouquets = Load();

        // Bouquets from before tokens were per bouquet.
        if (_bouquets.Any(bouquet => bouquet.Token.Length == 0))
        {
            _bouquets = [.. _bouquets.Select(bouquet => bouquet.Token.Length == 0 ? bouquet with { Token = Bouquet.NewToken() } : bouquet)];
            Save(_bouquets);
        }

        _database.Cleared += (_, everything) =>
        {
            if (everything)
            {
                _bouquets = [];
            }
        };
    }

    /// <summary>In their order.</summary>
    public IReadOnlyList<Bouquet> All => _bouquets;

    public Bouquet? Find(Guid id) => _bouquets.FirstOrDefault(bouquet => bouquet.Id == id);

    /// <summary>Whether a token opens this bouquet's playlist and guide.</summary>
    public bool Opens(Guid id, string? token) => Find(id) is { } bouquet && Matches(token, bouquet.Token);

    /// <summary>Whether a token opens this channel: that of a bouquet the channel is in.</summary>
    public bool Opens(ChannelKey channel, string? token) =>
        _bouquets.Any(bouquet => Matches(token, bouquet.Token) && bouquet.Channels.Contains(channel));

    public void Dispose() => _write.Dispose();

    public async Task<Bouquet> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        var clean = CleanName(name);
        var created = new Bouquet { Id = Guid.NewGuid(), Name = clean, Position = _bouquets.Count, UpdatedAt = DateTimeOffset.UtcNow };

        await ChangeAsync(list => [.. list, created], cancellationToken).ConfigureAwait(false);

        return created;
    }

    public Task RenameAsync(Guid id, string name, CancellationToken cancellationToken = default)
    {
        var clean = CleanName(name);

        return ChangeOneAsync(id, bouquet => bouquet with { Name = clean }, cancellationToken);
    }

    public Task SetChannelsAsync(Guid id, IEnumerable<ChannelKey> channels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channels);

        // A channel is in a bouquet once; a second entry would be a second number for it.
        var distinct = channels.Distinct().ToList();

        return ChangeOneAsync(id, bouquet => bouquet with { Channels = distinct }, cancellationToken);
    }

    /// <summary>Gives a bouquet a new token. Every address with the old one stops working.</summary>
    public Task NewTokenAsync(Guid id, CancellationToken cancellationToken = default) =>
        ChangeOneAsync(id, bouquet => bouquet with { Token = Bouquet.NewToken() }, cancellationToken);

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
        ChangeAsync(list => list.Where(bouquet => bouquet.Id != id), cancellationToken);

    /// <summary>Puts the bouquets in this order. Ones not named stay behind them.</summary>
    public Task ReorderAsync(IReadOnlyList<Guid> order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);

        var places = order.Select((id, index) => (id, index)).ToDictionary(place => place.id, place => place.index);

        return ChangeAsync(
            list => list.OrderBy(bouquet => places.GetValueOrDefault(bouquet.Id, int.MaxValue)).ThenBy(bouquet => bouquet.Position),
            cancellationToken);
    }

    /// <summary>In time that does not depend on where they differ.</summary>
    private static bool Matches(string? given, string expected) =>
        given is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected));

    private static string CleanName(string name)
    {
        var clean = name?.Trim() ?? string.Empty;

        if (clean.Length is 0 or > MaxNameLength)
        {
            throw new ArgumentException($"A bouquet name has 1 to {MaxNameLength} characters.", nameof(name));
        }

        return clean;
    }

    private Task ChangeOneAsync(Guid id, Func<Bouquet, Bouquet> change, CancellationToken cancellationToken) =>
        ChangeAsync(
            list => list.Select(bouquet => bouquet.Id == id ? change(bouquet) with { UpdatedAt = DateTimeOffset.UtcNow } : bouquet),
            cancellationToken);

    private async Task ChangeAsync(Func<IReadOnlyList<Bouquet>, IEnumerable<Bouquet>> change, CancellationToken cancellationToken)
    {
        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var next = change(_bouquets).Select((bouquet, index) => bouquet with { Position = index }).ToList();

            Save(next);

            _bouquets = next;
        }
        finally
        {
            _write.Release();
        }
    }

    private List<Bouquet> Load()
    {
        using var connection = _database.Open();
        using var channels = connection.CreateCommand();

        channels.CommandText = "SELECT bouquet, provider, channel FROM bouquet_channels ORDER BY bouquet, position;";

        var entries = new Dictionary<string, List<ChannelKey>>(StringComparer.Ordinal);

        using (var reader = channels.ExecuteReader())
        {
            while (reader.Read())
            {
                var bouquet = reader.GetString(0);

                if (!entries.TryGetValue(bouquet, out var list))
                {
                    entries[bouquet] = list = [];
                }

                list.Add(new ChannelKey(reader.GetString(1), reader.GetString(2)));
            }
        }

        using var bouquets = connection.CreateCommand();

        bouquets.CommandText = "SELECT id, name, position, updated_at, token FROM bouquets ORDER BY position;";

        var loaded = new List<Bouquet>();

        using (var reader = bouquets.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = reader.GetString(0);

                loaded.Add(new Bouquet
                {
                    Id = Guid.Parse(id),
                    Name = reader.GetString(1),
                    Position = reader.GetInt32(2),
                    UpdatedAt = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                    Token = reader.GetString(4),
                    Channels = entries.TryGetValue(id, out var list) ? list : [],
                });
            }
        }

        return loaded;
    }

    /// <summary>Replaces what is stored in one transaction, so a crash leaves the previous bouquets.</summary>
    private void Save(List<Bouquet> bouquets)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM bouquets;";
            clear.ExecuteNonQuery();
        }

        using var bouquet = connection.CreateCommand();

        bouquet.Transaction = transaction;
        bouquet.CommandText = "INSERT INTO bouquets (id, name, position, updated_at, token) VALUES ($id, $name, $position, $updated, $token);";

        var id = bouquet.Parameters.Add("$id", SqliteType.Text);
        var name = bouquet.Parameters.Add("$name", SqliteType.Text);
        var position = bouquet.Parameters.Add("$position", SqliteType.Integer);
        var updated = bouquet.Parameters.Add("$updated", SqliteType.Text);
        var token = bouquet.Parameters.Add("$token", SqliteType.Text);

        using var channel = connection.CreateCommand();

        channel.Transaction = transaction;
        channel.CommandText = "INSERT INTO bouquet_channels (bouquet, position, provider, channel) VALUES ($bouquet, $position, $provider, $channel);";

        var owner = channel.Parameters.Add("$bouquet", SqliteType.Text);
        var place = channel.Parameters.Add("$position", SqliteType.Integer);
        var provider = channel.Parameters.Add("$provider", SqliteType.Text);
        var channelId = channel.Parameters.Add("$channel", SqliteType.Text);

        foreach (var entry in bouquets)
        {
            id.Value = entry.Id.ToString("D");
            name.Value = entry.Name;
            position.Value = entry.Position;
            updated.Value = entry.UpdatedAt.ToString("O", CultureInfo.InvariantCulture);
            token.Value = entry.Token;
            bouquet.ExecuteNonQuery();

            owner.Value = id.Value;

            for (var index = 0; index < entry.Channels.Count; index++)
            {
                place.Value = index;
                provider.Value = entry.Channels[index].ProviderId;
                channelId.Value = entry.Channels[index].ChannelId;
                channel.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }
}
