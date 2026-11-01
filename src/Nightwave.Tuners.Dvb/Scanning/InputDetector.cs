using System.Diagnostics;
using System.Globalization;
using Nightwave.Core.Tuning;
using Nightwave.Tuners.Dvb.Devices;
using Nightwave.Tuners.Dvb.Mpeg.Si;
using Nightwave.Tuners.Dvb.Streaming;
using Nightwave.Tuners.Dvb.Transport;
using Nightwave.Tuners.Dvb.Tuning;
using Microsoft.Extensions.Logging;

namespace Nightwave.Tuners.Dvb.Scanning;

/// <summary>
/// Finds out which satellite is on which input of a SAT>IP server. The server's description
/// does not say - <c>X_SATIPCAP</c> only counts tuners - so each input (<c>src</c> 1 to 4) is
/// tuned to known transponders of the positions there are start tables for, and the first one
/// that locks is asked for its NIT: the orbital position in its satellite delivery descriptor is
/// the answer, even where it is a tenth of a degree off what the table says.
/// </summary>
/// <remarks>
/// <para>
/// Fast because the tuner says when it has no lock: its RTCP report is believed once
/// <see cref="ScanTimings.Lock"/> has passed, rather than waiting for a PAT that will not come.
/// An input stops being probed at the first position that is confirmed. One where the first
/// transponder of every position reports no signal at all is taken to have nothing connected,
/// without trying the rest.
/// </para>
/// <para>
/// One tuner at a time, taken from the pool like any other use: a detection never takes a
/// tuner somebody watches on.
/// </para>
/// </remarks>
internal sealed partial class InputDetector : IDisposable
{
    /// <summary>SAT>IP servers switch up to four inputs, as DiSEqC 1.0 does.</summary>
    public const int InputCount = 4;

    /// <summary>How long a detection is trusted. Dishes are rarely moved; once a week costs a few seconds of one tuner.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>How soon a detection that found nothing is tried again: somebody fixing the cable wants to see it work.</summary>
    public static readonly TimeSpan RetryAfterNothing = TimeSpan.FromMinutes(2);

    private readonly InputCache _cache;
    private readonly TunerPool _pool;
    private readonly ITsTransportFactory _transports;
    private readonly ScanTimings _timings;
    private readonly TimeProvider _time;
    private readonly ILogger<InputDetector> _logger;
    private readonly SemaphoreSlim _running = new(1, 1);

    public InputDetector(
        InputCache cache,
        TunerPool pool,
        ITsTransportFactory transports,
        ScanTimings timings,
        TimeProvider time,
        ILogger<InputDetector> logger)
    {
        _cache = cache;
        _pool = pool;
        _transports = transports;
        _timings = timings;
        _time = time;
        _logger = logger;
    }

    public void Dispose() => _running.Dispose();

    /// <summary>
    /// Whether what is known about a device's inputs should be detected again: never detected,
    /// only guessed from old settings, older than <see cref="MaxAge"/>, or nothing found a while ago.
    /// </summary>
    public bool IsDue(DeviceInputs? known)
    {
        if (known?.DetectedAt is not { } detectedAt)
        {
            return true;
        }

        var age = _time.GetUtcNow() - detectedAt;

        return age > MaxAge || (known.SatelliteInputs().Count == 0 && age > RetryAfterNothing);
    }

    /// <summary>Detects the inputs of a device with satellite tuners where <see cref="IsDue"/> says so.</summary>
    public async Task EnsureAsync(TunerDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (device.Capacity(DeliverySystem.Satellite) == 0
            || !IsDue(await _cache.GetAsync(device.Id, cancellationToken).ConfigureAwait(false)))
        {
            return;
        }

        await DetectAsync(device, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Detects every input of a device on a tuner of its own, and keeps the result. Where no
    /// tuner is free, what was known stays, with the reason next to it.
    /// </summary>
    public async Task<DeviceInputs> DetectAsync(TunerDevice device, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);

        await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var known = await _cache.GetAsync(device.Id, cancellationToken).ConfigureAwait(false);
            IAsyncDisposable reservation;

            try
            {
                reservation = _pool.ReserveForScan(device, DeliverySystem.Satellite, $"{device.Label}, input detection");
            }
            catch (TunerException error)
            {
                return await PostponeAsync(device, known, $"no tuner was free ({error.Message})", cancellationToken).ConfigureAwait(false);
            }

            await using (reservation.ConfigureAwait(false))
            {
                try
                {
                    return await DetectAllAsync(device, known, cancellationToken).ConfigureAwait(false);
                }
                catch (TunerTransportException error) when (error.Unreachable)
                {
                    return await PostponeAsync(device, known, $"the device could not be reached ({error.Message})", cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>Keeps what was known, with the reason the check did not happen.</summary>
    private async Task<DeviceInputs> PostponeAsync(TunerDevice device, DeviceInputs? known, string reason, CancellationToken cancellationToken)
    {
        var postponed = (known ?? new DeviceInputs()) with { Problem = $"the inputs could not be checked: {reason}" };

        LogDetectionPostponed(device.Label, reason);

        if (known is null || known.SatelliteInputs().Count == 0)
        {
            await _cache.SetAsync(device.Id, postponed, cancellationToken).ConfigureAwait(false);
        }

        return postponed;
    }

    private async Task<DeviceInputs> DetectAllAsync(TunerDevice device, DeviceInputs? known, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var inputs = new List<DetectedInput>();

        for (var source = 1; source <= device.InputCount; source++)
        {
            var guess = known?.Inputs.FirstOrDefault(input => input.Source == source)?.Position is { } tenths
                ? new OrbitalPosition(tenths)
                : (OrbitalPosition?)null;

            var input = await DetectInputAsync(device, source, guess, null, cancellationToken).ConfigureAwait(false);

            // The same satellite on several inputs is written the same way on all of them: the
            // way the first one has it, which may be a guess the channels carry in their ids.
            if (input.Position is { } found
                && inputs.FirstOrDefault(other => other.Position is { } earlier && earlier != found && Math.Abs(earlier - found) <= 1) is { } same)
            {
                input = input with { Position = same.Position };
            }

            inputs.Add(input);
        }

        var detected = new DeviceInputs { DetectedAt = _time.GetUtcNow(), Inputs = inputs };
        await _cache.SetAsync(device.Id, detected, cancellationToken).ConfigureAwait(false);

        var summary = string.Join(", ", inputs.Select(input => input.Position is { } position
            ? $"src={input.Source} {new OrbitalPosition(position)} ({input.Evidence})"
            : $"src={input.Source} none"));

        LogDetected(device.Label, clock.Elapsed.TotalSeconds, summary);

        return detected;
    }

    /// <summary>
    /// Detects one input on a tuner the caller holds. The position it had before, where there
    /// was one, is tried first. The result is kept in the cache as well.
    /// </summary>
    public async Task<DetectedInput> VerifyInputAsync(
        TunerDevice device,
        int source,
        OrbitalPosition? guess,
        Action<TuningParameters>? tuning,
        CancellationToken cancellationToken)
    {
        var input = await DetectInputAsync(device, source, guess, tuning, cancellationToken).ConfigureAwait(false);
        await _cache.SetInputAsync(device.Id, input, cancellationToken).ConfigureAwait(false);

        return input;
    }

    /// <summary>The satellite on one input, on a tuner the caller holds.</summary>
    internal async Task<DetectedInput> DetectInputAsync(
        TunerDevice device,
        int source,
        OrbitalPosition? guess,
        Action<TuningParameters>? tuning,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var failures = new List<string>();
        var anySignal = false;
        var anyReport = false;
        (OrbitalPosition Position, TuningParameters Tuning)? lockedOnly = null;

        foreach (var (round, position, candidate) in Candidates(guess))
        {
            // After the first transponder of every position: an input that reports no signal on
            // any of them has nothing connected, or a satellite there is no table for.
            if (round == 1 && anyReport && !anySignal)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            tuning?.Invoke(candidate);

            var transport = _transports.Create(device, new TuneRequest(candidate, source));
            NetworkProbeResult result;

            try
            {
                // One section with a satellite delivery descriptor says where the network is; the
                // rest of the NIT would only take longer.
                result = await NetworkProbe.ReadAsync(
                    transport,
                    _timings,
                    cancellationToken,
                    network => network.Any(stream => stream.OrbitalPosition is not null)).ConfigureAwait(false);
            }
            catch (TunerTransportException error) when (!error.Unreachable)
            {
                anyReport |= error.Signal is not null;
                anySignal |= error.Signal is { Level: > 0 } or { Locked: true };
                failures.Add($"{position} {candidate.Describe()}: {error.Message}");
                LogProbeFailed(device.Label, source, candidate, error.Message);
                continue;
            }
            finally
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }

            anySignal = true;

            if (OrbitalPositionOf(result.Network, candidate) is { } broadcast)
            {
                // The network says where it is. A guess within a tenth is kept as written, since
                // the channels found before carry it in their ids.
                var found = guess is { } known && Math.Abs(known.Tenths - broadcast) <= 1 ? known : new OrbitalPosition(broadcast);

                LogInputFound(device.Label, source, found, candidate, clock.Elapsed.TotalSeconds);

                return new DetectedInput { Source = source, Position = found.Tenths, Evidence = "nit", LockedOn = candidate };
            }

            lockedOnly ??= (position, candidate);
            failures.Add($"{position} {candidate.Describe()}: locked, but no NIT with a satellite position arrived");
        }

        if (lockedOnly is { } locked)
        {
            // Locked on a transponder of the table, but no network said where it is: the table is
            // the best there is.
            LogInputAssumed(device.Label, source, locked.Position.ToString(), locked.Tuning.Describe());

            return new DetectedInput { Source = source, Position = locked.Position.Tenths, Evidence = "lock", LockedOn = locked.Tuning };
        }

        var reason = anyReport && !anySignal
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"no signal at all on {failures.Count} transponders of {string.Join(", ", InitialTransponders.Positions)}; nothing is connected, or the dish points at a satellite Nightwave has no start transponders for")
            : $"nothing locked ({string.Join("; ", failures.Take(3))}{(failures.Count > 3 ? $"; {failures.Count - 3} more" : string.Empty)})";

        LogInputEmpty(device.Label, source, reason, clock.Elapsed.TotalSeconds);

        return new DetectedInput { Source = source, Failure = reason };
    }

    /// <summary>
    /// The transponders to try, round by round: the first of each position, then the second, and
    /// so on - so an input is known after one transponder per position in the common case, and a
    /// transponder that moved costs one probe. The guess, where there is one, goes first in
    /// every round.
    /// </summary>
    internal static IEnumerable<(int Round, OrbitalPosition Position, TuningParameters Tuning)> Candidates(OrbitalPosition? guess)
    {
        var positions = InitialTransponders.Positions.ToList();

        var guessed = guess is { } first ? positions.FindIndex(position => Math.Abs(position.Tenths - first.Tenths) <= 1) : -1;

        if (guessed > 0)
        {
            var known = positions[guessed];
            positions.RemoveAt(guessed);
            positions.Insert(0, known);
        }

        var rounds = positions.Max(position => InitialTransponders.For(position).Count);

        for (var round = 0; round < rounds; round++)
        {
            foreach (var position in positions)
            {
                var transponders = InitialTransponders.For(position);

                if (round < transponders.Count)
                {
                    yield return (round, position, transponders[round]);
                }
            }
        }
    }

    /// <summary>
    /// The orbital position the NIT gives the transponder that was tuned, or, where it does not
    /// list that one, the position most of its transport streams are on.
    /// </summary>
    internal static int? OrbitalPositionOf(IReadOnlyList<NetworkTransportStream> network, TuningParameters tuned)
    {
        var satellite = network.Where(stream => stream.OrbitalPosition is not null).ToList();

        if (satellite.FirstOrDefault(stream => stream.Tuning is { } tuning && tuning.IsSameTransponder(tuned)) is { } same)
        {
            return same.OrbitalPosition;
        }

        return satellite
            .GroupBy(stream => stream.OrbitalPosition!.Value)
            .OrderByDescending(group => group.Count())
            .Select(group => (int?)group.Key)
            .FirstOrDefault();
    }

    [LoggerMessage(EventId = 9822, Level = LogLevel.Information, Message = "Input src={Source} of {Device} receives {Position}: the NIT of {Transponder} says so ({Seconds:0.0} s).")]
    private partial void LogInputFound(string device, int source, OrbitalPosition position, TuningParameters transponder, double seconds);

    [LoggerMessage(EventId = 9823, Level = LogLevel.Warning, Message = "Input src={Source} of {Device} locked on {Transponder} of {Position}, but no NIT said which satellite it is; the table's position is assumed.")]
    private partial void LogInputAssumed(string device, int source, string position, string transponder);

    [LoggerMessage(EventId = 9824, Level = LogLevel.Information, Message = "No satellite found on input src={Source} of {Device} ({Seconds:0.0} s): {Reason}")]
    private partial void LogInputEmpty(string device, int source, string reason, double seconds);

    [LoggerMessage(EventId = 9825, Level = LogLevel.Debug, Message = "Probing {Transponder} on input src={Source} of {Device}: {Reason}")]
    private partial void LogProbeFailed(string device, int source, TuningParameters transponder, string reason);

    [LoggerMessage(EventId = 9826, Level = LogLevel.Information, Message = "Detected the inputs of {Device} in {Seconds:0.0} s: {Inputs}.")]
    private partial void LogDetected(string device, double seconds, string inputs);

    [LoggerMessage(EventId = 9827, Level = LogLevel.Warning, Message = "The inputs of {Device} were not checked: {Reason}. What was known before is kept; the check is repeated with the next scan.")]
    private partial void LogDetectionPostponed(string device, string reason);
}
