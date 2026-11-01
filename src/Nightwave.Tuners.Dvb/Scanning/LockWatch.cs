using System.Globalization;
using Nightwave.Tuners.Dvb.Transport;

namespace Nightwave.Tuners.Dvb.Scanning;

/// <summary>
/// Ends the wait for a transponder early when the tuner says it has no lock. Without it a scan
/// waits the whole time a program association table may take for every transponder that is not
/// there; with it, it knows as soon as the lock window has passed, and why.
/// </summary>
/// <remarks>
/// The tuner's report is not trusted before the window has passed: a SAT>IP server reports
/// level 0 and no lock right after the tune, while the front end is still switching inputs and
/// searching, and a Kathrein EXIP-418 has been seen to report the lock two seconds after the
/// first packets arrived. So data always wins over the report, and the report only counts once
/// the window is over.
/// </remarks>
internal sealed class LockWatch : IDisposable
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    private readonly Timer _timer;
    private volatile TunerSignal? _noLock;
    private volatile bool _disposed;

    /// <param name="transport">The tuner.</param>
    /// <param name="window">How long a lock may take.</param>
    /// <param name="hasData">Whether the transponder delivered what is waited for; checked from a timer thread.</param>
    /// <param name="deadline">Cancelled when the tuner reports no lock after the window.</param>
    public LockWatch(ITsTransport transport, TimeSpan window, Func<bool> hasData, CancellationTokenSource deadline)
    {
        _timer = new Timer(
            _ =>
            {
                if (_disposed || _noLock is not null || hasData())
                {
                    return;
                }

                if (transport.Status.Signal is { Locked: false } signal)
                {
                    _noLock = signal;

                    try
                    {
                        deadline.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // The wait is over anyway.
                    }
                }
            },
            null,
            window,
            Poll);
    }

    /// <summary>What the tuner reported when the watch gave up on it, or null while it has not.</summary>
    public TunerSignal? NoLock => _noLock;

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }

    /// <summary>
    /// Why a transponder delivered nothing, as precisely as the tuner lets us say: no lock with
    /// its level, a lock without data, or - from a server that sends no reports - only that
    /// nothing came.
    /// </summary>
    public static TunerTransportException Nothing(ITsTransport transport, TunerSignal? noLock, TimeSpan lockWindow, TimeSpan dataWindow, string what)
    {
        ArgumentNullException.ThrowIfNull(transport);

        if (noLock is not null)
        {
            return new TunerTransportException(
                string.Create(CultureInfo.InvariantCulture, $"{noLock.Describe()} after {lockWindow.TotalSeconds:0.#} seconds."))
            {
                Signal = noLock,
            };
        }

        var status = transport.Status;

        if (status.Signal is { Locked: true } locked)
        {
            return new TunerTransportException(string.Create(
                CultureInfo.InvariantCulture,
                $"The tuner reports {locked.Describe()}, but no {what} arrived within {dataWindow.TotalSeconds:0.#} seconds: nothing is broadcast on this transponder, or it is not a DVB transport stream."))
            {
                Signal = locked,
            };
        }

        if (status.Signal is { } unlocked)
        {
            return new TunerTransportException(string.Create(
                CultureInfo.InvariantCulture,
                $"{unlocked.Describe()}: no {what} arrived within {dataWindow.TotalSeconds:0.#} seconds."))
            {
                Signal = unlocked,
            };
        }

        return new TunerTransportException(string.Create(
            CultureInfo.InvariantCulture,
            $"No {what} arrived within {dataWindow.TotalSeconds:0.#} seconds: no lock on this transponder, or nothing is broadcast on it."));
    }
}
