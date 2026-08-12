using EFPerformanceAnalyzer.Api.Options;
using Microsoft.Extensions.Options;

namespace EFPerformanceAnalyzer.Api.Services;

public sealed class ScanTargetValidationResult
{
    public bool IsAllowed { get; init; }
    public string? Error { get; init; }
    public string? ResolvedPath { get; init; }

    public static ScanTargetValidationResult Allowed(string resolvedPath) =>
        new() { IsAllowed = true, ResolvedPath = resolvedPath };

    public static ScanTargetValidationResult Denied(string error) =>
        new() { IsAllowed = false, Error = error };
}

/// <summary>
/// Confines scan requests to an operator-configured allowlist of directories. Without this, an
/// HTTP-exposed "read any file path" endpoint is an arbitrary filesystem-disclosure primitive.
/// </summary>
public sealed class ScanTargetValidator(IOptions<ScanSettings> settings)
{
    public ScanTargetValidationResult Validate(string? requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            return ScanTargetValidationResult.Denied("targetPath is required.");

        var allowedRoots = settings.Value.AllowedRoots;
        if (allowedRoots.Count == 0)
        {
            return ScanTargetValidationResult.Denied(
                "No scan roots are configured. Set ScanSettings:AllowedRoots in appsettings.json before scanning.");
        }

        string resolvedPath;
        try
        {
            resolvedPath = Path.GetFullPath(requestedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ScanTargetValidationResult.Denied("targetPath is not a valid path.");
        }

        var isUnderAllowedRoot = allowedRoots.Any(root =>
        {
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return resolvedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                   resolvedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        });

        if (!isUnderAllowedRoot)
            return ScanTargetValidationResult.Denied("targetPath is outside the configured allowed scan roots.");

        if (!Directory.Exists(resolvedPath))
            return ScanTargetValidationResult.Denied("targetPath does not exist or is not a directory.");

        return ScanTargetValidationResult.Allowed(resolvedPath);
    }
}
