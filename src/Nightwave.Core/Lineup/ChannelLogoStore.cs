using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Nightwave.Core.Storage;
using Nightwave.Core.Tuning;

namespace Nightwave.Core.Lineup;

/// <summary>What is shown for a channel in place of what its tuner offers.</summary>
public enum ChannelLogoMode
{
    /// <summary>Whatever the tuner offers: its picon, or the logo in the playlist.</summary>
    Standard,

    /// <summary>No logo, even where the tuner has one.</summary>
    Removed,

    /// <summary>A picture kept here.</summary>
    File,

    /// <summary>A picture elsewhere, which players fetch themselves.</summary>
    Address,
}

/// <summary>The choice for one channel. Channels nobody touched have none.</summary>
public sealed record ChannelLogoChoice(ChannelLogoMode Mode, Uri? Address = null, ChannelLogo? Picture = null);

/// <summary>
/// The logos chosen by hand, one per channel: read once, then answered from memory. They are not
/// part of the cache, as nothing would bring them back, but they go with a reset of the database.
/// </summary>
public sealed class ChannelLogoStore : IDisposable
{
    /// <summary>The largest picture taken: more than a logo needs, and all of it goes through a browser.</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    private readonly NightwaveDatabase _database;
    private readonly SemaphoreSlim _write = new(1, 1);
    private volatile Dictionary<ChannelKey, ChannelLogoChoice> _choices;

    public ChannelLogoStore(NightwaveDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);

        _database = database;
        _choices = Load();
        _database.Cleared += (_, everything) =>
        {
            if (everything)
            {
                _choices = [];
            }
        };
    }

    public void Dispose() => _write.Dispose();

    public ChannelLogoChoice? Find(ChannelKey channel) => _choices.GetValueOrDefault(channel);

    /// <summary>Back to what the tuner offers.</summary>
    public Task ResetAsync(ChannelKey channel, CancellationToken cancellationToken = default) => ChangeAsync(channel, null, cancellationToken);

    public Task RemoveAsync(ChannelKey channel, CancellationToken cancellationToken = default) =>
        ChangeAsync(channel, new ChannelLogoChoice(ChannelLogoMode.Removed), cancellationToken);

    /// <summary>Takes a picture that players fetch from elsewhere.</summary>
    /// <exception cref="ArgumentException">The address is not an absolute HTTP one.</exception>
    public Task SetAddressAsync(ChannelKey channel, string address, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("A logo's address starts with http:// or https://.", nameof(address));
        }

        return ChangeAsync(channel, new ChannelLogoChoice(ChannelLogoMode.Address, uri), cancellationToken);
    }

    /// <summary>Keeps a picture here.</summary>
    /// <exception cref="ArgumentException">It is too large, or no PNG, JPEG or WebP.</exception>
    public Task SetFileAsync(ChannelKey channel, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        if (content.Length > MaxBytes)
        {
            throw new ArgumentException($"A logo has at most {MaxBytes / 1048576} MB.", nameof(content));
        }

        // By what the file is, not what it calls itself. SVG is left out on purpose: it can carry
        // scripts, and this address is on the same origin as the interface.
        var type = PictureType(content.Span) ?? throw new ArgumentException("A logo is a PNG, JPEG or WebP picture.", nameof(content));

        return ChangeAsync(channel, new ChannelLogoChoice(ChannelLogoMode.File, Picture: Picture(content.ToArray(), type)), cancellationToken);
    }

    /// <summary>The media type a picture really has, or null for anything else.</summary>
    public static string? PictureType(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith<byte>([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (content.StartsWith<byte>([0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }

        return content.Length >= 12 && content.StartsWith("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8) ? "image/webp" : null;
    }

    private static ChannelLogo Picture(byte[] content, string type) =>
        new(content, type, Convert.ToHexStringLower(SHA256.HashData(content))[..16]);

    private async Task ChangeAsync(ChannelKey channel, ChannelLogoChoice? choice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);

        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Save(channel, choice);

            var next = new Dictionary<ChannelKey, ChannelLogoChoice>(_choices);

            if (choice is null)
            {
                next.Remove(channel);
            }
            else
            {
                next[channel] = choice;
            }

            _choices = next;
        }
        finally
        {
            _write.Release();
        }
    }

    private void Save(ChannelKey channel, ChannelLogoChoice? choice)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = choice is null
            ? "DELETE FROM channel_logos WHERE provider = $provider AND channel = $channel;"
            : """
              INSERT INTO channel_logos (provider, channel, mode, address, content_type, content)
              VALUES ($provider, $channel, $mode, $address, $type, $content)
              ON CONFLICT (provider, channel) DO UPDATE
              SET mode = excluded.mode, address = excluded.address, content_type = excluded.content_type, content = excluded.content;
              """;
        command.Parameters.AddWithValue("$provider", channel.ProviderId);
        command.Parameters.AddWithValue("$channel", channel.ChannelId);

        if (choice is not null)
        {
            command.Parameters.AddWithValue("$mode", (int)choice.Mode);
            command.Parameters.AddWithValue("$address", (object?)choice.Address?.ToString() ?? DBNull.Value);
            command.Parameters.AddWithValue("$type", (object?)choice.Picture?.ContentType ?? DBNull.Value);
            command.Parameters.Add("$content", SqliteType.Blob).Value = (object?)choice.Picture?.Content.ToArray() ?? DBNull.Value;
        }

        command.ExecuteNonQuery();
    }

    private Dictionary<ChannelKey, ChannelLogoChoice> Load()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = "SELECT provider, channel, mode, address, content_type, content FROM channel_logos;";

        var choices = new Dictionary<ChannelKey, ChannelLogoChoice>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var mode = (ChannelLogoMode)reader.GetInt32(2);

            choices[new ChannelKey(reader.GetString(0), reader.GetString(1))] = new ChannelLogoChoice(
                mode,
                reader.IsDBNull(3) ? null : new Uri(reader.GetString(3)),
                reader.IsDBNull(5) ? null : Picture((byte[])reader.GetValue(5), reader.GetString(4)));
        }

        return choices;
    }
}
