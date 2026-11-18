using System.Text;

namespace NeoHub.Services
{
    /// <summary>
    /// Crash-safe file replacement: write a temporary file in the same directory, flush it to
    /// disk, then rename it over the target. A rename within one directory is atomic, so a
    /// reader (including the settings file watcher with reloadOnChange) sees either the old
    /// file or the new one, never a truncated or half-written file, even after a power cut.
    ///
    /// Plain File.WriteAllText truncates first and writes second; a reload or crash in between
    /// leaves an empty or partial userSettings.json, which loses the panel's encryption keys.
    /// </summary>
    internal static class AtomicFile
    {
        public static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        {
            var tempPath = TempPathFor(path);
            try
            {
                await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                                 FileShare.None, bufferSize: 4096, FileOptions.Asynchronous))
                {
                    await stream.WriteAsync(bytes, ct);
                    await stream.FlushAsync(ct);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, path, overwrite: true);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        public static Task WriteAllTextAsync(string path, string contents, CancellationToken ct = default)
            => WriteAllBytesAsync(path, Utf8NoBom.GetBytes(contents), ct);

        /// <summary>Synchronous variant for startup code that runs before the host.</summary>
        public static void WriteAllText(string path, string contents)
        {
            var tempPath = TempPathFor(path);
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(Utf8NoBom.GetBytes(contents));
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, path, overwrite: true);
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }

        // Same encoding File.WriteAllText uses by default.
        private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

        private static string TempPathFor(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath)!;
            Directory.CreateDirectory(directory);
            // Same directory: the rename must not cross filesystems (Docker volumes, bind mounts).
            return Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch { /* best effort */ }
        }
    }
}
