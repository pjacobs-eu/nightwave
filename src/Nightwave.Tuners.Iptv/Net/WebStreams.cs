using Nightwave.Core.Streaming;
using Nightwave.Core.Tuning;

namespace Nightwave.Tuners.Iptv.Net;

/// <summary>
/// Who asks for a stream that may replace their running one, and what that one is told. Clients
/// have to be told apart by <paramref name="Client"/>.
/// </summary>
public sealed record StreamClaim(string Client, Exception Reason)
{
    /// <summary>The claim of a request, where it makes one.</summary>
    public static StreamClaim? For(TunedStreamRequest request, string providerId, string channel, string tuner)
    {
        ArgumentNullException.ThrowIfNull(request);

        return request.ReplacesOwnStream
            ? new StreamClaim(
                request.ClientDeviceId,
                new TunerException(
                    providerId,
                    "tuner.replaced_by_own_stream",
                    $"A stream on '{tuner}' ended: the same client opened '{channel}' at the tuner's limit, and that channel took over its place.",
                    "To receive both channels from one address, free another place on the tuner first.")
                {
                    Values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["channel"] = channel, ["tuner"] = tuner },
                })
            : null;
    }
}

/// <summary>
/// The web streams running right now: one connection per channel however many watch it, and
/// a place on the tuner for each of those connections.
/// </summary>
public sealed class WebStreams
{
    private readonly ConnectionGate _gate = new();
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Running> _running = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many connections run on a tuner.</summary>
    public int ActiveOn(string tunerId) => _gate.ActiveOn(tunerId);

    /// <summary>
    /// Joins the channel's connection, or takes a place for a new one. At the tuner's limit, a
    /// client takes over the place of their one running stream. Otherwise null: there is no
    /// queue, whoever asks is told.
    /// </summary>
    /// <param name="tunerId">The tuner the channel is on.</param>
    /// <param name="channelId">The channel.</param>
    /// <param name="limit">Null is a tuner with no known limit.</param>
    /// <param name="open">Connects to the stream.</param>
    /// <param name="claim">Null where this stream may not replace any other.</param>
    public ITunedStream? TryOpen(string tunerId, string channelId, int? limit, Func<CancellationToken, Task<Stream>> open, StreamClaim? claim = null)
    {
        lock (_lock)
        {
            if (_running.TryGetValue(channelId, out var running))
            {
                running.Clients.Add(claim?.Client);

                return new Viewer(this, channelId, running, claim?.Client);
            }

            IDisposable place;

            if (_gate.TryEnter(tunerId, limit) is { } free)
            {
                place = free;
            }
            else if (claim is not null && OwnStream(tunerId, claim.Client) is { } own)
            {
                // The provider counts the old connection until it is closed, so the new one
                // waits for that; and the place itself goes straight from one to the other.
                _running.Remove(own.Key);
                own.Value.Replaced = true;
                place = own.Value.Place;

                var closed = Task.Run(() => own.Value.Upstream.EndAsync(claim.Reason).AsTask());
                var connect = open;

                open = async token =>
                {
                    await closed.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

                    return await connect(token).ConfigureAwait(false);
                };
            }
            else
            {
                return null;
            }

            running = new Running(tunerId, new SharedUpstream(open), place);
            running.Clients.Add(claim?.Client);
            _running[channelId] = running;

            return new Viewer(this, channelId, running, claim?.Client);
        }
    }

    /// <summary>
    /// A client's stream on a tuner, where it is their only one and nobody else watches it: two
    /// streams are somebody watching both, and nobody else's picture ends for this client.
    /// </summary>
    private KeyValuePair<string, Running>? OwnStream(string tunerId, string client)
    {
        var own = _running.Where(entry => entry.Value.TunerId == tunerId && entry.Value.Clients.Contains(client)).ToList();

        return own is [{ Value.Clients.Count: 1 } only] ? only : null;
    }

    private async ValueTask LeaveAsync(string channelId, Running running, string? client)
    {
        lock (_lock)
        {
            running.Clients.Remove(client);

            // A replaced stream's connection and place have gone on already.
            if (running.Clients.Count > 0 || running.Replaced)
            {
                return;
            }

            _running.Remove(channelId);
        }

        await running.Upstream.DisposeAsync().ConfigureAwait(false);
        running.Place.Dispose();
    }

    private sealed class Running(string tunerId, SharedUpstream upstream, IDisposable place)
    {
        public string TunerId { get; } = tunerId;

        public SharedUpstream Upstream { get; } = upstream;

        public IDisposable Place { get; } = place;

        /// <summary>One entry per viewer; null for a viewer whose stream may not replace others.</summary>
        public List<string?> Clients { get; } = [];

        public bool Replaced { get; set; }
    }

    private sealed class Viewer(WebStreams owner, string channelId, Running running, string? client) : ITunedStream
    {
        private int _disposed;

        public Guid Id { get; } = Guid.NewGuid();

        public string ChannelId => channelId;

        public bool IsActive => Volatile.Read(ref _disposed) == 0;

        public Task<Stream> OpenReadAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(!IsActive, this);

            return running.Upstream.SubscribeAsync(cancellationToken);
        }

        public ValueTask DisposeAsync() =>
            Interlocked.Exchange(ref _disposed, 1) == 0 ? owner.LeaveAsync(channelId, running, client) : ValueTask.CompletedTask;
    }
}
