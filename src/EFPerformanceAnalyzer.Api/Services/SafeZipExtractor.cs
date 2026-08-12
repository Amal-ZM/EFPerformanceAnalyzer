using System.IO.Compression;

namespace EFPerformanceAnalyzer.Api.Services;

/// <summary>
/// Extracts an uploaded .zip into a directory, guarding against the two classic risks of
/// extracting an untrusted archive: zip-slip (an entry path like "../../evil.cs" writing outside
/// the target directory) and decompression bombs (an archive that expands to far more data than
/// its compressed size suggests).
/// </summary>
public static class SafeZipExtractor
{
    private const int MaxEntries = 50_000;

    public static void ExtractSafely(Stream zipStream, string destinationDir, long maxTotalBytes)
    {
        var destinationFullPath = Path.GetFullPath(destinationDir) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(destinationDir);

        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        if (archive.Entries.Count > MaxEntries)
            throw new InvalidOperationException($"Archive contains {archive.Entries.Count} entries, exceeding the limit of {MaxEntries}.");

        long totalBytesWritten = 0;

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue; // directory entry

            var entryFullPath = Path.GetFullPath(Path.Combine(destinationDir, entry.FullName));
            if (!entryFullPath.StartsWith(destinationFullPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Archive entry '{entry.FullName}' resolves outside the extraction directory.");

            Directory.CreateDirectory(Path.GetDirectoryName(entryFullPath)!);

            using var entryStream = entry.Open();
            using var fileStream = new FileStream(entryFullPath, FileMode.Create, FileAccess.Write);

            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = entryStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                totalBytesWritten += bytesRead;
                if (totalBytesWritten > maxTotalBytes)
                {
                    throw new InvalidOperationException(
                        $"Archive expands past the {maxTotalBytes} byte limit; aborted (possible decompression bomb).");
                }

                fileStream.Write(buffer, 0, bytesRead);
            }
        }
    }
}
