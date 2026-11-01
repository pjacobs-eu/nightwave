using System.Text.Json;
using Nightwave.Core.Json;

namespace Nightwave.Core.Configuration;

/// <summary>
/// The configuration: read once, replaced whole, written atomically. Readers get a snapshot they
/// may keep; a change is made on a copy, so nobody ever sees half of an edit.
/// </summary>
public sealed class ConfigStore : IDisposable
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly SemaphoreSlim _write = new(1, 1);
    private volatile NightwaveConfig _current;

    public ConfigStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        DataDirectory = dataDirectory;
        _path = Path.Combine(dataDirectory, "config.json");
        _current = Load(_path);
    }

    /// <summary>Raised after a change was written. On the saving thread; handlers do no I/O.</summary>
    public event EventHandler? Changed;

    public string DataDirectory { get; }

    public NightwaveConfig Current => _current;

    public void Dispose() => _write.Dispose();

    /// <summary>Applies a change to a copy of the configuration and stores it.</summary>
    public async Task UpdateAsync(Action<NightwaveConfig> change, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(change);

        await _write.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var copy = JsonSerializer.Deserialize<NightwaveConfig>(JsonSerializer.Serialize(_current, Options), Options)!;

            change(copy);

            await AtomicJsonFile.WriteAsync(_path, copy, Options, cancellationToken).ConfigureAwait(false);
            RestrictToOwner(_path);

            _current = copy;
        }
        finally
        {
            _write.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static NightwaveConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new NightwaveConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<NightwaveConfig>(File.ReadAllText(path), Options) ?? new NightwaveConfig();
        }
        catch (JsonException error)
        {
            // Starting empty would look like every tuner and password had been deleted, and the
            // next save would make that true.
            throw new InvalidDataException(
                $"The configuration at {path} cannot be read ({error.Message}). Repair the file or restore a backup; nightwaved does not start with it like this.",
                error);
        }
    }

    /// <summary>The file holds provider passwords, so it is nobody else's to read.</summary>
    private static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
