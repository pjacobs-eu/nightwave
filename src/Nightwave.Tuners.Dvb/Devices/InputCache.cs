using Nightwave.Core.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nightwave.Tuners.Dvb.Tuning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Nightwave.Tuners.Dvb.Devices;

/// <summary>What was detected on the inputs of one device, and when.</summary>
internal sealed record DeviceInputs
{
    /// <summary>
    /// When the inputs were last detected. Null for guesses taken over from the settings of an
    /// earlier version, which nothing has checked yet.
    /// </summary>
    public DateTimeOffset? DetectedAt { get; init; }

    /// <summary>One entry per input that was probed, found or not.</summary>
    public IReadOnlyList<DetectedInput> Inputs { get; init; } = [];

    /// <summary>English reason when the detection could not run at all, for example because every tuner was busy.</summary>
    public string? Problem { get; init; }

    /// <summary>The inputs a satellite was found on.</summary>
    public IReadOnlyList<SatelliteInput> SatelliteInputs() =>
    [
        .. Inputs
            .Where(input => input.Position is not null)
            .OrderBy(input => input.Source)
            .Select(input => new SatelliteInput(input.Source, new OrbitalPosition(input.Position!.Value)) { LockedOn = input.LockedOn }),
    ];

    /// <summary>Why no satellite was found, input by input, in English.</summary>
    public string DescribeFailures() =>
        Problem ?? string.Join("; ", Inputs
            .Where(input => input.Position is null)
            .OrderBy(input => input.Source)
            .Select(input => $"input {input.Source}: {input.Failure ?? "nothing locked"}"));
}

/// <summary>One input of a device: the satellite on it, or why none was found.</summary>
internal sealed record DetectedInput
{
    public required int Source { get; init; }

    /// <summary>Orbital position in tenths of a degree, east positive. Null when nothing was found.</summary>
    public int? Position { get; init; }

    /// <summary>What the position rests on: <c>nit</c>, <c>lock</c> or <c>settings</c>.</summary>
    public string? Evidence { get; init; }

    /// <summary>The transponder that locked.</summary>
    public TuningParameters? LockedOn { get; init; }

    /// <summary>English reason when nothing was found on this input.</summary>
    public string? Failure { get; init; }
}

/// <summary>
/// The detected inputs of every device, kept as <c>inputs.json</c> in the plugin's data
/// directory so a restart does not have to tune again before a channel can be watched. Only a
/// cache: a file that cannot be read is detected again, not an error.
/// </summary>
internal sealed partial class InputCache : IDisposable
{
    public const string FileName = "inputs.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string? _path;
    private readonly ILogger<InputCache> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private Dictionary<string, DeviceInputs>? _devices;

    /// <param name="directory">Where to keep the file; null keeps it in memory only.</param>
    public InputCache(string? directory, ILogger<InputCache>? logger = null)
    {
        _path = directory is null ? null : Path.Combine(directory, FileName);
        _logger = logger ?? NullLogger<InputCache>.Instance;
    }

    public void Dispose() => _mutex.Dispose();

    /// <summary>Every device's inputs, by device id.</summary>
    public async Task<IReadOnlyDictionary<string, DeviceInputs>> GetAllAsync(CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return new Dictionary<string, DeviceInputs>(await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async Task<DeviceInputs?> GetAsync(string deviceId, CancellationToken cancellationToken) =>
        (await GetAllAsync(cancellationToken).ConfigureAwait(false)).GetValueOrDefault(deviceId);

    /// <summary>Replaces what is known about a device.</summary>
    public Task SetAsync(string deviceId, DeviceInputs inputs, CancellationToken cancellationToken) =>
        UpdateAsync(devices => devices[deviceId] = inputs, cancellationToken);

    /// <summary>
    /// Replaces what is known about one input of a device, leaving the others as they were. When
    /// the whole device was last detected stays what it was: one input checked is not all of them.
    /// </summary>
    public Task SetInputAsync(string deviceId, DetectedInput input, CancellationToken cancellationToken) =>
        UpdateAsync(
            devices =>
            {
                var known = devices.GetValueOrDefault(deviceId) ?? new DeviceInputs();
                devices[deviceId] = known with
                {
                    Inputs = [.. known.Inputs.Where(other => other.Source != input.Source), input],
                    Problem = null,
                };
            },
            cancellationToken);

    private async Task UpdateAsync(Action<Dictionary<string, DeviceInputs>> change, CancellationToken cancellationToken)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var devices = await LoadUnlockedAsync(cancellationToken).ConfigureAwait(false);
            var before = JsonSerializer.Serialize(devices, Json);

            change(devices);

            if (_path is null || JsonSerializer.Serialize(devices, Json) == before)
            {
                return;
            }

            try
            {
                await SaveUnlockedAsync(_path, devices, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Kept in memory; after a restart the inputs are detected again.
                LogWriteFailed(_path, error.Message);
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task<Dictionary<string, DeviceInputs>> LoadUnlockedAsync(CancellationToken cancellationToken)
    {
        if (_devices is { } cached)
        {
            return cached;
        }

        _devices = new Dictionary<string, DeviceInputs>(StringComparer.OrdinalIgnoreCase);

        if (_path is null || !File.Exists(_path))
        {
            return _devices;
        }

        try
        {
            var stream = File.OpenRead(_path);
            await using (stream.ConfigureAwait(false))
            {
                var document = await JsonSerializer.DeserializeAsync<Document>(stream, Json, cancellationToken).ConfigureAwait(false);

                foreach (var (deviceId, inputs) in document?.Devices ?? [])
                {
                    _devices[deviceId] = inputs;
                }
            }
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            LogReadFailed(_path, error.Message);
        }

        return _devices;
    }

    private static Task SaveUnlockedAsync(string path, Dictionary<string, DeviceInputs> devices, CancellationToken cancellationToken) =>
        AtomicJsonFile.WriteAsync(path, new Document { Devices = devices }, Json, cancellationToken);

    [LoggerMessage(EventId = 9820, Level = LogLevel.Warning, Message = "The detected device inputs at '{Path}' cannot be read ({Reason}); they are detected again at the next scan.")]
    private partial void LogReadFailed(string path, string reason);

    [LoggerMessage(EventId = 9821, Level = LogLevel.Warning, Message = "The detected device inputs could not be written to '{Path}': {Reason}. They are kept until the server restarts and detected again after that.")]
    private partial void LogWriteFailed(string path, string reason);

    /// <summary>The file.</summary>
    private sealed record Document
    {
        public int Version { get; init; } = 1;

        public Dictionary<string, DeviceInputs> Devices { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
