namespace Nightwave.Tuners.Iptv.Net;

/// <summary>
/// Counts the streams running on each account.
/// </summary>
/// <remarks>
/// A provider counts every open stream against one limit, a channel and a film alike, and what
/// it does past that limit is end one that was already running - usually the one somebody is
/// watching. So the count is kept here, across live television and the libraries both, and a
/// stream past the limit is refused before it is asked for.
/// </remarks>
public sealed class ConnectionGate
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly Dictionary<string, int> _active = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>How many streams run on an account right now.</summary>
    public int ActiveOn(string serverId)
    {
        lock (_lock)
        {
            return _active.GetValueOrDefault(serverId);
        }
    }

    /// <summary>
    /// Takes a place on an account where one is free. The place is given back by disposing what
    /// comes back, exactly once however often that happens.
    /// </summary>
    /// <param name="limit">Null is an account the provider sets no known limit on.</param>
    public IDisposable? TryEnter(string serverId, int? limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        lock (_lock)
        {
            var active = _active.GetValueOrDefault(serverId);

            if (limit is { } max && active >= max)
            {
                return null;
            }

            _active[serverId] = active + 1;
        }

        return new Place(this, serverId);
    }

    /// <summary>
    /// Takes a place, waiting a short while for one to come free.
    /// </summary>
    /// <remarks>
    /// Only for reads that open and close in quick succession - a player jumping from the start of
    /// a film to its index at the end, where the old connection is still being taken down as the
    /// new one is asked for. A wait long enough to cover that and no longer: somebody else's
    /// running stream is not something to queue behind, and past it the read fails and says so.
    /// </remarks>
    public async Task<IDisposable?> EnterAsync(
        string serverId,
        int? limit,
        TimeSpan patience,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + patience;

        while (true)
        {
            if (TryEnter(serverId, limit) is { } place)
            {
                return place;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return null;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Exit(string serverId)
    {
        lock (_lock)
        {
            var remaining = _active.GetValueOrDefault(serverId) - 1;

            if (remaining <= 0)
            {
                _active.Remove(serverId);
            }
            else
            {
                _active[serverId] = remaining;
            }
        }
    }

    private sealed class Place(ConnectionGate gate, string serverId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Exit(serverId);
            }
        }
    }
}
