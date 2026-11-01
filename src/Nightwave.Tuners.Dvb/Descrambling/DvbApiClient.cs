using System.Collections.Concurrent;
using Nightwave.Core.Tuning;
using Microsoft.Extensions.Logging;

namespace Nightwave.Tuners.Dvb.Descrambling;

/// <summary>
/// Nightwave as a network dvbapi client of one descrambling server: one TCP connection carrying every
/// service descrambled at the same time, and the reconnecting when it drops.
/// </summary>
/// <remarks>
/// <para>
/// One connection for all services rather than one per service, for three reasons found in the
/// reference implementation's module-dvbapi.c. The server keeps the protocol version of its
/// network clients in a single variable (<c>last_client_proto_version</c>) that it resets to 0
/// whenever any client disconnects and applies to every open connection when reading - with one
/// connection per service, zapping away from one channel would garble the others. A CA PMT with
/// list management "only" or "first" stops every other service of the same connection, so a
/// shared connection has to add services with "add", which is what the server's list management
/// was built for. And a closed connection stops all of its services in the server, which makes
/// cleaning up after a crash or a lost network automatic.
/// </para>
/// <para>
/// Messages from the server are routed to a service by the adapter index in them, which is the
/// device number of the service's slot (see <see cref="CaProgramMap.DeviceNumber"/>). The connection is opened for the first
/// service and closed after the last one; while there are services, a dropped connection is
/// reopened with a growing delay (1 s doubling up to 30 s) and every service's CA PMT sent again,
/// since the server forgot them all when the connection went.
/// </para>
/// </remarks>
internal sealed partial class DvbApiClient : IAsyncDisposable
{
    private readonly DvbApiOptions _options;
    private readonly DvbApiTimings _timings;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<int, DvbApiServiceDescrambler> _services = new();
    private readonly CancellationTokenSource _lifetime = new();
    private DvbApiConnection? _connection;
    private Task _supervisor = Task.CompletedTask;

    public DvbApiClient(DvbApiOptions options, DvbApiTimings timings, ILogger logger, TimeProvider time)
    {
        _options = options;
        _timings = timings;
        _logger = logger;
        _time = time;
        Endpoint = $"{options.Host}:{options.Port}";
    }

    public string Endpoint { get; }

    public DvbApiTimings Timings => _timings;

    public bool IsConnected => Volatile.Read(ref _connection) is { IsAlive: true };

    public string? ServerVersion => Volatile.Read(ref _connection)?.ServerVersion;

    public long Dropped => Volatile.Read(ref _connection)?.Dropped ?? 0;

    /// <summary>
    /// Registers the service and hands its CA PMT to the server, connecting first if need be. Fails
    /// with a <see cref="TunerException"/> naming the server when it cannot be reached.
    /// </summary>
    public async Task AddServiceAsync(DvbApiServiceDescrambler service, CancellationToken cancellationToken)
    {
        DvbApiServiceDescrambler[] reconnected = [];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_services.TryAdd(service.Slot, service))
            {
                throw new InvalidOperationException(
                    $"Slot {service.Slot} is already descrambling '{_services[service.Slot].ChannelName}'; the tuner gave it out twice.");
            }

            try
            {
                var connection = _connection;
                if (connection is { IsAlive: true })
                {
                    SendCaPmt(connection, service, DvbApiProtocol.ListManagementAdd);
                }
                else
                {
                    // Also where the connection was lost and is waiting for its next attempt: the
                    // channel is wanted now, and the other services come back with it.
                    reconnected = await OpenConnectionLockedAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                _services.TryRemove(new KeyValuePair<int, DvbApiServiceDescrambler>(service.Slot, service));
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }

        Notify(reconnected, static service => service.OnConnected());
    }

    /// <summary>Tells the server the service's program map changed.</summary>
    public void UpdateService(DvbApiServiceDescrambler service)
    {
        var connection = Volatile.Read(ref _connection);
        if (connection is not null && _services.TryGetValue(service.Slot, out var registered) && registered == service)
        {
            SendCaPmt(connection, service, DvbApiProtocol.ListManagementUpdate);
        }
    }

    /// <summary>Stops the service in the server; closes the connection after the last one.</summary>
    public async Task RemoveServiceAsync(DvbApiServiceDescrambler service)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_services.TryRemove(new KeyValuePair<int, DvbApiServiceDescrambler>(service.Slot, service)))
            {
                return;
            }

            var connection = _connection;
            if (connection is null)
            {
                return;
            }

            var version = connection.ProtocolVersion;
            var device = CaProgramMap.DeviceNumber(service.Slot);
            connection.Send(id => DvbApiProtocol.EncodeCaStop(version, id, device));

            if (_services.IsEmpty)
            {
                Volatile.Write(ref _connection, null);
                await connection.CloseAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                await connection.DisposeAsync().ConfigureAwait(false);
                LogDisconnected(Endpoint);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Hands a section that matched the server's filter back to it. Never blocks.</summary>
    public void SendFilterData(int demux, int filter, ReadOnlySpan<byte> section)
    {
        var connection = Volatile.Read(ref _connection);
        if (connection is null)
        {
            return;
        }

        var version = connection.ProtocolVersion;
        var copy = section.ToArray();
        connection.Send(id => DvbApiProtocol.EncodeFilterData(version, id, demux, filter, copy));
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        var connection = Interlocked.Exchange(ref _connection, null);
        if (connection is not null)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            await _supervisor.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }

        _lifetime.Dispose();
        _gate.Dispose();
    }

    private static void SendCaPmt(DvbApiConnection connection, DvbApiServiceDescrambler service, byte listManagement)
    {
        var version = connection.ProtocolVersion;
        var body = service.BuildCaPmt(listManagement);
        connection.Send(id => DvbApiProtocol.EncodeCaPmt(version, id, body));
    }

    private void Dispatch(DvbApiServerMessage message)
    {
        if (message.Adapter < 0)
        {
            return;
        }

        if (_services.TryGetValue(CaProgramMap.SlotOfDevice(message.Adapter), out var service))
        {
            service.OnMessage(message);
        }
        else
        {
            LogUnroutable(Endpoint, message.GetType().Name, message.Adapter);
        }
    }

    /// <summary>
    /// Opens the connection and hands the server the CA PMT of every registered service. Call with
    /// the gate held; returns the services, to be told once the gate is released.
    /// </summary>
    private async Task<DvbApiServiceDescrambler[]> OpenConnectionLockedAsync(CancellationToken cancellationToken)
    {
        var connection = await DvbApiConnection.OpenAsync(_options, _timings, Dispatch, _logger, cancellationToken)
            .ConfigureAwait(false);
        Volatile.Write(ref _connection, connection);
        _supervisor = SuperviseAsync(connection);

        var services = _services.Values.OrderBy(service => service.Slot).ToArray();
        foreach (var service in services)
        {
            SendCaPmt(connection, service, DvbApiProtocol.ListManagementAdd);
        }

        return services;
    }

    /// <summary>
    /// Tells services about the connection. Never with the gate held: their events reach the
    /// tuner, which may well close a service in return.
    /// </summary>
    private static void Notify(IEnumerable<DvbApiServiceDescrambler> services, Action<DvbApiServiceDescrambler> action)
    {
        foreach (var service in services)
        {
            action(service);
        }
    }

    /// <summary>
    /// Watches one connection. When it drops while services still need it, tells them and
    /// reconnects; the new connection gets a watcher of its own.
    /// </summary>
    private async Task SuperviseAsync(DvbApiConnection connection)
    {
        var reason = await connection.Closed.ConfigureAwait(false);
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        DvbApiServiceDescrambler[] waiting;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(Volatile.Read(ref _connection), connection))
            {
                return; // closed on purpose after the last service
            }

            Volatile.Write(ref _connection, null);
            waiting = [.. _services.Values];
            LogConnectionLost(Endpoint, reason, waiting.Length);
        }
        finally
        {
            _gate.Release();
        }

        Notify(waiting, service => service.OnConnectionLost(reason));
        await connection.DisposeAsync().ConfigureAwait(false);
        await ReconnectAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Tries until it works, somebody else reconnected, the services are gone, or the client is
    /// disposed.
    /// </summary>
    private async Task ReconnectAsync()
    {
        var delay = _timings.ReconnectInitialDelay;
        while (true)
        {
            try
            {
                await Task.Delay(delay, _time, _lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var next = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _timings.ReconnectMaxDelay.Ticks));
            DvbApiServiceDescrambler[]? reconnected = null;
            TunerException? failure = null;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_services.IsEmpty || _lifetime.IsCancellationRequested || _connection is { IsAlive: true })
                {
                    return;
                }

                try
                {
                    reconnected = await OpenConnectionLockedAsync(_lifetime.Token).ConfigureAwait(false);
                    LogReconnected(Endpoint, reconnected.Length);
                }
                catch (TunerException exception)
                {
                    LogReconnectFailed(Endpoint, exception.Message, next.TotalSeconds);
                    failure = exception;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
            finally
            {
                _gate.Release();
            }

            if (reconnected is not null)
            {
                Notify(reconnected, static service => service.OnConnected());
                return;
            }

            Notify(_services.Values, service => service.OnReconnectFailed(failure!));
            delay = next;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost the connection to the descrambling server at {Endpoint} ({Reason}); {Services} services wait for it to come back")]
    private partial void LogConnectionLost(string endpoint, string reason, int services);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconnected to the descrambling server at {Endpoint}, sent the CA PMT of {Services} services again")]
    private partial void LogReconnected(string endpoint, int services);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reconnecting to the descrambling server at {Endpoint} failed: {Reason}. Trying again after {Seconds} s")]
    private partial void LogReconnectFailed(string endpoint, string reason, double seconds);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Closed the connection to the descrambling server at {Endpoint}: no scrambled service left")]
    private partial void LogDisconnected(string endpoint);

    [LoggerMessage(Level = LogLevel.Debug, Message = "the descrambling server at {Endpoint} sent {Message} for adapter {Adapter}, which is no service of this connection (any more)")]
    private partial void LogUnroutable(string endpoint, string message, int adapter);
}
