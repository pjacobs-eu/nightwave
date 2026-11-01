using System.Security.Cryptography;
using System.Buffers.Text;
using System.Text;

namespace Nightwave.Core.Lineup;

/// <summary>
/// Which channel, across every tuner. Ids are only unique inside the provider that issued them.
/// Compared without regard to case, the way the providers compare them.
/// </summary>
public sealed record ChannelKey(string ProviderId, string ChannelId)
{
    /// <summary>The key as one URL segment. Never carries credentials: channel ids do not contain any.</summary>
    public string Encoded => Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{ProviderId}\n{ChannelId}"));

    public static bool TryDecode(string? encoded, out ChannelKey key)
    {
        key = new ChannelKey(string.Empty, string.Empty);

        if (string.IsNullOrEmpty(encoded) || !Base64Url.IsValid(encoded))
        {
            return false;
        }

        var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(encoded)).Split('\n', 2);

        if (parts.Length != 2)
        {
            return false;
        }

        key = new ChannelKey(parts[0], parts[1]);

        return true;
    }

    public bool Equals(ChannelKey? other) =>
        other is not null
        && string.Equals(ProviderId, other.ProviderId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(ChannelId, other.ChannelId, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(ProviderId),
        StringComparer.OrdinalIgnoreCase.GetHashCode(ChannelId));

    public override string ToString() => $"{ProviderId}/{ChannelId}";
}

/// <summary>
/// A named, ordered list of channels from any tuners, the way a satellite receiver keeps them.
/// Each bouquet comes out as a playlist and a guide of its own.
/// </summary>
public sealed record Bouquet
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required int Position { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>
    /// What players show to get at this bouquet: its playlist, its guide, and the streams and
    /// logos of its channels. One bouquet can be handed on and withdrawn without touching another.
    /// </summary>
    public string Token { get; init; } = NewToken();

    /// <summary>
    /// The channels in the order somebody put them in. Kept as saved, including channels that are
    /// gone for now: a tuner that is off for an evening must not empty a bouquet.
    /// </summary>
    public IReadOnlyList<ChannelKey> Channels { get; init; } = [];

    public static string NewToken() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
