using System.Text;

namespace ClickZen.Core.Persistence;

/// <summary>
/// Crash-safe file writes: data is written to a temp file in the same directory, flushed,
/// then swapped into place with <see cref="File.Replace(string, string, string?)"/> / <see cref="File.Move(string, string, bool)"/>.
/// A reader therefore always sees either the complete old file or the complete new file.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents) =>
        WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(contents));

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> bytes)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (IOException) { /* best effort */ }
            }
        }
    }

    public static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                             FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await fs.WriteAsync(bytes, ct);
                await fs.FlushAsync(ct);
            }

            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); } catch (IOException) { /* best effort */ }
            }
        }
    }
}
