namespace Nightwave.Core.Tuning;

/// <summary>Something a tuner backend keeps running for as long as the daemon does.</summary>
public interface IBackgroundWork
{
    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
