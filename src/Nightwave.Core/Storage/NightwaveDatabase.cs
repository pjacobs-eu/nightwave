using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Nightwave.Core.Storage;

/// <summary>
/// The database: channels, guide and bouquets, in one SQLite file in the data directory. The
/// configuration stays a JSON file, small enough to read and repair by hand.
/// </summary>
/// <remarks>
/// Connections are short-lived and pooled; write-ahead logging lets the guide be read while a
/// batch of programmes is being written.
/// </remarks>
public sealed class NightwaveDatabase
{
    public const string FileName = "nightwave.db";

    /// <summary>In order; the file's <c>user_version</c> says how many of them have run.</summary>
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE epg (
            network INTEGER NOT NULL,
            transport_stream INTEGER NOT NULL,
            service INTEGER NOT NULL,
            event INTEGER NOT NULL,
            start_at INTEGER NOT NULL,
            end_at INTEGER NOT NULL,
            data TEXT NOT NULL,
            PRIMARY KEY (network, transport_stream, service, event)
        ) WITHOUT ROWID;
        CREATE INDEX epg_end ON epg (end_at);

        CREATE TABLE web_guide (
            tuner TEXT NOT NULL,
            guide_id TEXT NOT NULL,
            start_at INTEGER NOT NULL,
            end_at INTEGER NOT NULL,
            data TEXT NOT NULL,
            PRIMARY KEY (tuner, guide_id, start_at)
        ) WITHOUT ROWID;
        CREATE TABLE web_guide_fetch (
            tuner TEXT PRIMARY KEY,
            fetched_at INTEGER NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE satip_channels (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            data TEXT NOT NULL
        ) WITHOUT ROWID;
        CREATE TABLE satip_scan_targets (
            id TEXT PRIMARY KEY,
            scanned_at TEXT NOT NULL
        ) WITHOUT ROWID;

        CREATE TABLE bouquets (
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            position INTEGER NOT NULL,
            updated_at TEXT NOT NULL
        ) WITHOUT ROWID;
        CREATE TABLE bouquet_channels (
            bouquet TEXT NOT NULL REFERENCES bouquets (id) ON DELETE CASCADE,
            position INTEGER NOT NULL,
            provider TEXT NOT NULL,
            channel TEXT NOT NULL,
            PRIMARY KEY (bouquet, position)
        ) WITHOUT ROWID;
        """,
        "ALTER TABLE bouquets ADD COLUMN token TEXT NOT NULL DEFAULT '';",
        """
        CREATE TABLE channel_logos (
            provider TEXT NOT NULL,
            channel TEXT NOT NULL,
            mode INTEGER NOT NULL,
            address TEXT,
            content_type TEXT,
            content BLOB,
            PRIMARY KEY (provider, channel)
        ) WITHOUT ROWID;
        """,
    ];

    private static readonly string[] CacheTables = ["epg", "web_guide", "web_guide_fetch"];

    private static readonly string[] AllTables =
        [.. CacheTables, "satip_channels", "satip_scan_targets", "bouquet_channels", "bouquets", "channel_logos"];

    private readonly string _connectionString;

    /// <summary>
    /// Raised after tables were emptied, so whoever keeps a copy in memory lets go of it.
    /// The argument is true when everything went, false when only the cache did.
    /// </summary>
    public event EventHandler<bool>? Cleared;

    public NightwaveDatabase(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(directory, FileName),
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();

        Migrate();
    }

    /// <summary>An open connection. Dispose it when done; it goes back to the pool.</summary>
    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);

        connection.Open();
        Execute(connection, "PRAGMA synchronous = NORMAL;");

        return connection;
    }

    /// <summary>Empties the guide, which fills up again by itself.</summary>
    public void ClearCache() => Clear(CacheTables, everything: false);

    /// <summary>Empties everything: guide, channels, bouquets and chosen logos. The configuration is not in here and stays.</summary>
    public void Reset() => Clear(AllTables, everything: true);

    /// <summary>The size of the database file and its write-ahead log, in bytes.</summary>
    public long SizeInBytes
    {
        get
        {
            var path = new SqliteConnectionStringBuilder(_connectionString).DataSource;

            return new[] { path, path + "-wal" }.Where(File.Exists).Sum(file => new FileInfo(file).Length);
        }
    }

    public static void Execute(SqliteConnection connection, string sql)
    {
        ArgumentNullException.ThrowIfNull(connection);

        using var command = connection.CreateCommand();

        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void Clear(string[] tables, bool everything)
    {
        using (var connection = Open())
        {
            using (var transaction = connection.BeginTransaction())
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = string.Concat(tables.Select(table => $"DELETE FROM {table};"));
                command.ExecuteNonQuery();
                transaction.Commit();
            }

            // Deleting leaves the pages in the file; this gives the disk back.
            Execute(connection, "VACUUM; PRAGMA wal_checkpoint(TRUNCATE);");
        }

        Cleared?.Invoke(this, everything);
    }

    private void Migrate()
    {
        using var connection = Open();

        Execute(connection, "PRAGMA journal_mode = WAL;");

        using var version = connection.CreateCommand();

        version.CommandText = "PRAGMA user_version;";

        var applied = Convert.ToInt32(version.ExecuteScalar(), CultureInfo.InvariantCulture);

        for (var index = applied; index < Migrations.Length; index++)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();

            command.Transaction = transaction;
            command.CommandText = string.Create(CultureInfo.InvariantCulture, $"{Migrations[index]}\nPRAGMA user_version = {index + 1};");
            command.ExecuteNonQuery();

            transaction.Commit();
        }
    }
}
