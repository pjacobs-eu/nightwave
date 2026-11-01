using Nightwave.Core.Streaming;
using Nightwave.Core.Tuning;

namespace Nightwave.Server;

/// <summary>Starts what the tuner backends keep running, and stops it with the daemon.</summary>
internal sealed class BackgroundWorkHost(IEnumerable<IBackgroundWork> work) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(work.Select(item => item.StartAsync(cancellationToken)));

    public Task StopAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(work.Select(item => item.StopAsync(cancellationToken)));
}

/// <summary>Takes a sample of every running stream once a second, for the monitor's history.</summary>
internal sealed class TelemetrySampler(StreamHub hub) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            hub.Sample();
        }
    }
}
