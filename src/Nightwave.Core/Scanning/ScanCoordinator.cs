using Microsoft.Extensions.Logging;
using Nightwave.Core.Tuning;

namespace Nightwave.Core.Scanning;

public enum ScanState
{
    Running,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>One scan as the UI shows it. A snapshot; asked for again rather than changed.</summary>
public sealed record ScanStatus
{
    public required string ProviderId { get; init; }

    public required string TargetName { get; init; }

    public required ScanState State { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public ScanProgress? Progress { get; init; }

    public ScanResult? Result { get; init; }

    public string? Error { get; init; }

    public string? Remedy { get; init; }
}

/// <summary>
/// Runs channel scans: one per provider, on the server, so closing the page stops nothing. The
/// last outcome is kept for whoever comes back.
/// </summary>
public sealed partial class ScanCoordinator
{
    private readonly IReadOnlyList<IChannelScanner> _scanners;
    private readonly ILogger<ScanCoordinator> _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ScanStatus> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CancellationTokenSource> _running = new(StringComparer.OrdinalIgnoreCase);

    public ScanCoordinator(IEnumerable<IChannelScanner> scanners, ILogger<ScanCoordinator> logger)
    {
        _scanners = [.. scanners];
        _logger = logger;
    }

    /// <summary>Raised whenever a scan moved. Handlers do no I/O.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<IChannelScanner> Scanners => _scanners;

    public ScanStatus? Status(string providerId)
    {
        lock (_gate)
        {
            return _status.GetValueOrDefault(providerId);
        }
    }

    /// <summary>Starts a scan, or refuses if the provider already runs one.</summary>
    public bool TryStart(IChannelScanner scanner, ScanRequest request, string targetName)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(request);

        var cancellation = new CancellationTokenSource();

        lock (_gate)
        {
            if (_running.ContainsKey(scanner.ProviderId))
            {
                cancellation.Dispose();

                return false;
            }

            _running[scanner.ProviderId] = cancellation;
            _status[scanner.ProviderId] = new ScanStatus
            {
                ProviderId = scanner.ProviderId,
                TargetName = targetName,
                State = ScanState.Running,
                StartedAt = DateTimeOffset.UtcNow,
            };
        }

        Changed?.Invoke(this, EventArgs.Empty);
        _ = Task.Run(() => RunAsync(scanner, request, cancellation), CancellationToken.None);

        return true;
    }

    public void Cancel(string providerId)
    {
        lock (_gate)
        {
            if (_running.TryGetValue(providerId, out var running))
            {
                running.Cancel();
            }
        }
    }

    private async Task RunAsync(IChannelScanner scanner, ScanRequest request, CancellationTokenSource cancellation)
    {
        var progress = new SynchronousProgress(update => Update(scanner.ProviderId, status => status with { Progress = update }));

        try
        {
            var result = await scanner.ScanAsync(request, progress, cancellation.Token).ConfigureAwait(false);

            Update(scanner.ProviderId, status => status with { State = ScanState.Completed, Result = result });
        }
        catch (OperationCanceledException)
        {
            Update(scanner.ProviderId, status => status with { State = ScanState.Cancelled });
        }
        catch (NightwaveException error)
        {
            Update(scanner.ProviderId, status => status with { State = ScanState.Failed, Error = error.Message, Remedy = error.Remedy });
        }
        catch (Exception error)
        {
            LogScanFailed(error, scanner.ProviderId);
            Update(scanner.ProviderId, status => status with { State = ScanState.Failed, Error = error.Message });
        }
        finally
        {
            lock (_gate)
            {
                _running.Remove(scanner.ProviderId);
            }

            cancellation.Dispose();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Update(string providerId, Func<ScanStatus, ScanStatus> change)
    {
        lock (_gate)
        {
            _status[providerId] = change(_status[providerId]);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    [LoggerMessage(EventId = 110, Level = LogLevel.Error, Message = "The channel scan of {Provider} failed.")]
    private partial void LogScanFailed(Exception error, string provider);

    /// <summary>On the scan's own thread: <see cref="Progress{T}"/> would post updates out of order.</summary>
    private sealed class SynchronousProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
}
