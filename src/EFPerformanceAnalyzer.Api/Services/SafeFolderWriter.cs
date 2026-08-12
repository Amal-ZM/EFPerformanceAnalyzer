namespace EFPerformanceAnalyzer.Api.Services;

/// <summary>
/// Writes a browser folder-picker upload (a set of files, each carrying its original relative
/// path via <c>webkitRelativePath</c>) into a destination directory. Applies the same
/// path-containment guard as SafeZipExtractor — a relative path is just as capable of encoding
/// "../../evil.cs" as a zip entry name is, and it's equally untrusted client input.
/// </summary>
public static class SafeFolderWriter
{
    private static readonly string[] ExcludedSegments = ["bin", "obj", "node_modules", ".git", ".vs"];

    public static async Task<int> WriteSafelyAsync(
        IReadOnlyList<IFormFile> files, string destinationDir, long maxTotalBytes, CancellationToken cancellationToken)
    {
        var destinationFullPath = Path.GetFullPath(destinationDir) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(destinationDir);

        long totalBytes = 0;
        var writtenCount = 0;

        foreach (var file in files)
        {
            var relativePath = file.FileName.Replace('\\', '/');
            if (!relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                continue;
            if (relativePath.Split('/').Any(segment => ExcludedSegments.Contains(segment, StringComparer.OrdinalIgnoreCase)))
                continue;

            var entryFullPath = Path.GetFullPath(Path.Combine(destinationDir, relativePath));
            if (!entryFullPath.StartsWith(destinationFullPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Uploaded file '{file.FileName}' resolves outside the target directory.");

            totalBytes += file.Length;
            if (totalBytes > maxTotalBytes)
                throw new InvalidOperationException($"Upload exceeds the {maxTotalBytes} byte limit.");

            Directory.CreateDirectory(Path.GetDirectoryName(entryFullPath)!);

            await using var fileStream = new FileStream(entryFullPath, FileMode.Create, FileAccess.Write);
            await using var sourceStream = file.OpenReadStream();
            await sourceStream.CopyToAsync(fileStream, cancellationToken);

            writtenCount++;
        }

        return writtenCount;
    }
}
