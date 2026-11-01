using System.Text.Json;
using Microsoft.Data.Sqlite;
using Nightwave.Core.Storage;
using Nightwave.Core.Tuning;

namespace Nightwave.Tuners.Iptv.Guide;

/// <summary>
/// The guides web tuners fetched, in the database. A provider's feed is regularly hundreds of
/// megabytes of XML, and this keeps only the offered channels, out of memory.
/// </summary>
public sealed class WebGuideStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly NightwaveDatabase _database;

    public WebGuideStore(NightwaveDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
    }

    public DateTimeOffset? FetchedAt(string tunerId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = "SELECT fetched_at FROM web_guide_fetch WHERE tuner = $tuner;";
        command.Parameters.AddWithValue("$tuner", tunerId);

        return command.ExecuteScalar() is long seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }

    /// <summary>Replaces a tuner's guide in one transaction: whoever reads sees the old one or the new one.</summary>
    public void Replace(string tunerId, IEnumerable<Programme> programmes, DateTimeOffset fetchedAt)
    {
        ArgumentNullException.ThrowIfNull(programmes);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();

        using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM web_guide WHERE tuner = $tuner;";
            clear.Parameters.AddWithValue("$tuner", tunerId);
            clear.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();

        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR REPLACE INTO web_guide (tuner, guide_id, start_at, end_at, data)
            VALUES ($tuner, $guide, $start, $end, $data);
            """;
        insert.Parameters.AddWithValue("$tuner", tunerId);

        var guide = insert.Parameters.Add("$guide", SqliteType.Text);
        var start = insert.Parameters.Add("$start", SqliteType.Integer);
        var end = insert.Parameters.Add("$end", SqliteType.Integer);
        var data = insert.Parameters.Add("$data", SqliteType.Text);

        foreach (var programme in programmes)
        {
            guide.Value = programme.ChannelId;
            start.Value = programme.StartsAt.ToUnixTimeSeconds();
            end.Value = programme.EndsAt.ToUnixTimeSeconds();
            data.Value = JsonSerializer.Serialize(programme, Json);
            insert.ExecuteNonQuery();
        }

        MarkFetched(connection, transaction, tunerId, fetchedAt);

        transaction.Commit();
    }

    /// <summary>Notes a fetch without touching the guide: a failed one is not tried again on every read.</summary>
    public void MarkFetched(string tunerId, DateTimeOffset at)
    {
        using var connection = _database.Open();

        MarkFetched(connection, null, tunerId, at);
    }

    public IReadOnlyList<Programme> Query(string guideId, DateTimeOffset from, DateTimeOffset to)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT data FROM web_guide
            WHERE guide_id = $guide AND end_at > $from AND start_at < $to
            ORDER BY start_at;
            """;
        command.Parameters.AddWithValue("$guide", guideId);
        command.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds());

        var programmes = new List<Programme>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            programmes.Add(JsonSerializer.Deserialize<Programme>(reader.GetString(0), Json)!);
        }

        return programmes;
    }

    private static void MarkFetched(SqliteConnection connection, SqliteTransaction? transaction, string tunerId, DateTimeOffset at)
    {
        using var mark = connection.CreateCommand();

        mark.Transaction = transaction;
        mark.CommandText = "INSERT OR REPLACE INTO web_guide_fetch (tuner, fetched_at) VALUES ($tuner, $at);";
        mark.Parameters.AddWithValue("$tuner", tunerId);
        mark.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
        mark.ExecuteNonQuery();
    }
}
