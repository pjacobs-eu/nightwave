using System.Text.Json;

namespace Nightwave.Core.Json;

/// <summary>
/// Writes one of the JSON files - the channel list, the detected inputs, a transport
/// stream's guide - so that a crash in the middle of a write leaves the previous file rather
/// than half of a new one: written to a temporary name beside it, flushed to the disk, then
/// moved over the old one.
/// </summary>
public static class AtomicJsonFile
{
    public static async Task WriteAsync<T>(string path, T value, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temporary = path + ".tmp";

        try
        {
            var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None);

            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, value, options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            // A half-written temporary is of no use to anybody, and left behind it would be
            // taken for a write in progress by whoever looks at the directory next.
            Delete(temporary);

            throw;
        }
    }

    private static void Delete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The write already failed; this is the tidying up after it.
        }
    }
}
