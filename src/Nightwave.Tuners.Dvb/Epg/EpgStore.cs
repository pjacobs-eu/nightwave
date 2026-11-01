using System.Text.Json;
using Microsoft.Data.Sqlite;
using Nightwave.Core.Storage;

namespace Nightwave.Tuners.Dvb.Epg;

/// <summary>
/// The guide from the broadcast: every service's programmes, in the database.
/// </summary>
/// <remarks>
/// Bounded in time: from a day back to <see cref="Horizon"/> ahead, which is more than any
/// satellite schedule reaches; anything outside is dropped as it goes stale.
/// <para>
/// <see cref="Upsert"/> is called from a tuner's reading loop, which may never wait for a disk,
/// so it only notes what arrived. A flush writes that in one transaction, and a query writes it
/// first, so whoever asks sees everything that came in.
/// </para>
/// </remarks>
internal sealed class EpgStore : IDisposable
{
    /// <summary>How far back programmes are kept, for somebody who looks at what just ended.</summary>
    public static readonly TimeSpan History = TimeSpan.FromDays(1);

    /// <summary>How far ahead programmes are kept.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(14);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly NightwaveDatabase _database;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _io = new(1, 1);

    /// <summary>What arrived since the last write, in order: a later event may replace an earlier one.</summary>
    private Dictionary<(DvbServiceKey Service, int Event), EpgEvent> _pending = [];

    private int _pruned;

    public EpgStore(NightwaveDatabase database, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
        _time = time;
        _database.Cleared += (_, _) =>
        {
            lock (_gate)
            {
                _pending = [];
            }
        };
    }

    public void Dispose() => _io.Dispose();

    /// <summary>
    /// Takes programmes of one service. An event replaces the one with its id; an event that
    /// now covers the time of another one of the same service replaces that one too, because the
    /// schedule changed under it.
    /// </summary>
    public void Upsert(DvbServiceKey service, IEnumerable<EpgEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var now = _time.GetUtcNow();
        var earliest = now - History;
        var latest = now + Horizon;

        lock (_gate)
        {
            foreach (var programme in events)
            {
                if (programme.End >= earliest && programme.Start <= latest)
                {
                    _pending.Remove((service, programme.EventId));
                    _pending[(service, programme.EventId)] = programme;
                }
            }
        }
    }

    /// <summary>The programmes of a service that overlap the window, by start time.</summary>
    public IReadOnlyList<EpgEvent> Query(DvbServiceKey service, DateTimeOffset from, DateTimeOffset to)
    {
        Write();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT data FROM epg
            WHERE network = $network AND transport_stream = $stream AND service = $service AND end_at > $from AND start_at < $to
            ORDER BY start_at;
            """;
        Bind(command, service);
        command.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());

        var programmes = new List<EpgEvent>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            programmes.Add(JsonSerializer.Deserialize<EpgEvent>(reader.GetString(0), Json)!);
        }

        return programmes;
    }

    /// <summary>The services there is at least one programme for.</summary>
    public IReadOnlyList<DvbServiceKey> Services()
    {
        Write();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = "SELECT DISTINCT network, transport_stream, service FROM epg;";

        var services = new List<DvbServiceKey>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            services.Add(new DvbServiceKey(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)));
        }

        return services;
    }

    /// <summary>Drops what ended more than a day ago. Returns how many programmes went.</summary>
    public int Prune()
    {
        Write();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM epg WHERE end_at < $earliest;";
        command.Parameters.AddWithValue("$earliest", (_time.GetUtcNow() - History).ToUnixTimeSeconds());

        return command.ExecuteNonQuery();
    }

    /// <summary>Clears out what went stale while the daemon was off, once. Later calls do nothing.</summary>
    public Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.Exchange(ref _pruned, 1) == 0)
        {
            Prune();
        }

        return Task.CompletedTask;
    }

    /// <summary>Writes what arrived since the last write.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _io.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await Task.Run(WriteUnlocked, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _io.Release();
        }
    }

    private static void Bind(SqliteCommand command, DvbServiceKey service)
    {
        command.Parameters.AddWithValue("$network", service.OriginalNetworkId);
        command.Parameters.AddWithValue("$stream", service.TransportStreamId);
        command.Parameters.AddWithValue("$service", service.ServiceId);
    }

    private void Write()
    {
        _io.Wait();

        try
        {
            WriteUnlocked();
        }
        finally
        {
            _io.Release();
        }
    }

    private void WriteUnlocked()
    {
        Dictionary<(DvbServiceKey Service, int Event), EpgEvent> pending;

        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            pending = _pending;
            _pending = [];
        }

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var ((service, eventId), programme) in pending)
        {
            using var overlapping = connection.CreateCommand();

            overlapping.Transaction = transaction;
            overlapping.CommandText = """
                DELETE FROM epg
                WHERE network = $network AND transport_stream = $stream AND service = $service
                  AND event <> $event AND start_at < $end AND end_at > $start;
                """;

            using var upsert = connection.CreateCommand();

            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT INTO epg (network, transport_stream, service, event, start_at, end_at, data)
                VALUES ($network, $stream, $service, $event, $start, $end, $data)
                ON CONFLICT (network, transport_stream, service, event) DO UPDATE
                SET start_at = excluded.start_at, end_at = excluded.end_at, data = excluded.data
                WHERE data <> excluded.data;
                """;

            foreach (var command in new[] { overlapping, upsert })
            {
                Bind(command, service);
                command.Parameters.AddWithValue("$event", eventId);
                command.Parameters.AddWithValue("$start", programme.Start.ToUnixTimeSeconds());
                command.Parameters.AddWithValue("$end", programme.End.ToUnixTimeSeconds());
            }

            upsert.Parameters.AddWithValue("$data", JsonSerializer.Serialize(programme, Json));

            overlapping.ExecuteNonQuery();
            upsert.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
