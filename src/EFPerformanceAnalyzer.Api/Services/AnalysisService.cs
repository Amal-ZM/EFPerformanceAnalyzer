using EFPerformanceAnalyzer.Api.Contracts;
using EFPerformanceAnalyzer.Api.Options;
using EFPerformanceAnalyzer.Api.Persistence;
using EFPerformanceAnalyzer.Core;
using EFPerformanceAnalyzer.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EFPerformanceAnalyzer.Api.Services;

public sealed class ScanRejectedException(string reason) : Exception(reason);

public sealed class AnalysisService(
    AnalyzerDbContext db,
    ScanTargetValidator validator,
    IOptions<ScanSettings> settings,
    ILogger<AnalysisService> logger)
{
    public async Task<AnalysisRunSummaryResponse> RunScanAsync(string? requestedPath, CancellationToken cancellationToken)
    {
        var validation = validator.Validate(requestedPath);
        if (!validation.IsAllowed)
            throw new ScanRejectedException(validation.Error!);

        return await AnalyzeAndPersistAsync(validation.ResolvedPath!, validation.ResolvedPath!, cancellationToken);
    }

    /// <summary>
    /// Scans an uploaded .zip of a project rather than a path already reachable on this machine's
    /// filesystem. The archive is extracted into an isolated temp directory (never under
    /// ScanSettings:AllowedRoots — it doesn't need to be, since we control that directory ourselves)
    /// and deleted again once the scan completes, win or lose.
    /// </summary>
    public async Task<AnalysisRunSummaryResponse> RunUploadScanAsync(
        Stream zipStream, string originalFileName, CancellationToken cancellationToken)
    {
        var extractionDir = Path.Combine(Path.GetTempPath(), "ef-analyzer-uploads", Guid.NewGuid().ToString("N"));

        try
        {
            logger.LogInformation("Extracting uploaded archive '{FileName}' to {ExtractionDir}", originalFileName, extractionDir);

            var maxTotalBytes = settings.Value.MaxUploadSizeBytes * settings.Value.MaxUploadExpansionRatio;
            try
            {
                SafeZipExtractor.ExtractSafely(zipStream, extractionDir, maxTotalBytes);
            }
            catch (InvalidDataException)
            {
                throw new ScanRejectedException("The uploaded file is not a valid .zip archive.");
            }
            catch (InvalidOperationException ex)
            {
                throw new ScanRejectedException(ex.Message);
            }

            return await AnalyzeAndPersistAsync(extractionDir, $"upload:{originalFileName}", cancellationToken, stripPathPrefix: extractionDir);
        }
        finally
        {
            if (Directory.Exists(extractionDir))
            {
                try
                {
                    Directory.Delete(extractionDir, recursive: true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to clean up extraction directory {ExtractionDir}", extractionDir);
                }
            }
        }
    }

    /// <summary>
    /// Scans a folder picked directly in the browser (an &lt;input webkitdirectory&gt; or a
    /// drag-and-drop folder drop), sent as individual files rather than a zip. Each file's
    /// relative path is preserved via its form-part filename.
    /// </summary>
    public async Task<AnalysisRunSummaryResponse> RunFolderUploadScanAsync(
        IReadOnlyList<IFormFile> files, string? folderLabel, CancellationToken cancellationToken)
    {
        if (files.Count == 0)
            throw new ScanRejectedException("No files were uploaded.");
        if (files.Count > settings.Value.MaxFilesPerScan)
        {
            throw new ScanRejectedException(
                $"Upload contains {files.Count} files, exceeding the configured limit of {settings.Value.MaxFilesPerScan}.");
        }

        var extractionDir = Path.Combine(Path.GetTempPath(), "ef-analyzer-uploads", Guid.NewGuid().ToString("N"));

        try
        {
            logger.LogInformation("Writing {FileCount} uploaded files to {ExtractionDir}", files.Count, extractionDir);

            int written;
            try
            {
                written = await SafeFolderWriter.WriteSafelyAsync(
                    files, extractionDir, settings.Value.MaxUploadSizeBytes, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                throw new ScanRejectedException(ex.Message);
            }

            if (written == 0)
                throw new ScanRejectedException("None of the uploaded files were .cs source files.");

            var label = string.IsNullOrWhiteSpace(folderLabel) ? "folder-upload" : folderLabel;
            return await AnalyzeAndPersistAsync(extractionDir, $"upload:{label}", cancellationToken, stripPathPrefix: extractionDir);
        }
        finally
        {
            if (Directory.Exists(extractionDir))
            {
                try
                {
                    Directory.Delete(extractionDir, recursive: true);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to clean up extraction directory {ExtractionDir}", extractionDir);
                }
            }
        }
    }

    private async Task<AnalysisRunSummaryResponse> AnalyzeAndPersistAsync(
        string resolvedPath, string displayTargetPath, CancellationToken cancellationToken, string? stripPathPrefix = null)
    {
        var maxFiles = settings.Value.MaxFilesPerScan;
        var timeout = TimeSpan.FromSeconds(settings.Value.ScanTimeoutSeconds);

        logger.LogInformation("Starting EF Core analysis of {TargetPath}", resolvedPath);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var analyzer = new SolutionAnalyzer();
        AnalysisReport report;
        try
        {
            report = await Task.Run(() => analyzer.Analyze(resolvedPath, maxFiles), linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new ScanRejectedException($"Scan exceeded the {timeout.TotalSeconds}s timeout and was aborted.");
        }
        catch (InvalidOperationException ex)
        {
            throw new ScanRejectedException(ex.Message);
        }

        var runEntity = new AnalysisRunEntity
        {
            TargetPath = Truncate(displayTargetPath, 1000),
            StartedAtUtc = report.StartedAtUtc,
            CompletedAtUtc = report.CompletedAtUtc,
            FilesScanned = report.FilesScanned,
            DbContextsFound = report.DbContextsFound,
            EntityTypesFound = report.EntityTypesFound,
            SuppressedCount = report.SuppressedCount,
            Findings = report.Findings.Select(f => new FindingEntity
            {
                Category = f.Category.ToString(),
                Severity = f.Severity.ToString(),
                FilePath = Truncate(StripPrefix(f.FilePath, stripPathPrefix), 1000),
                Line = f.Line,
                MemberName = Truncate(f.MemberName, 500),
                Message = Truncate(f.Message, 1000),
                CodeSnippet = Truncate(f.CodeSnippet, 300),
                Recommendation = f.Recommendation is null ? null : Truncate(f.Recommendation, 1000)
            }).ToList()
        };

        db.AnalysisRuns.Add(runEntity);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Completed analysis of {TargetPath}: {FindingCount} findings across {FileCount} files",
            resolvedPath, report.Findings.Count, report.FilesScanned);

        return ToSummary(runEntity);
    }

    public async Task<IReadOnlyList<AnalysisRunSummaryResponse>> GetRunsAsync(CancellationToken cancellationToken)
    {
        var runs = await db.AnalysisRuns
            .Include(r => r.Findings)
            .OrderByDescending(r => r.StartedAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);

        return runs.Select(ToSummary).ToList();
    }

    public async Task<AnalysisRunDetailResponse?> GetRunDetailAsync(int runId, CancellationToken cancellationToken)
    {
        var run = await db.AnalysisRuns
            .Include(r => r.Findings)
            .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);

        if (run is null)
            return null;

        return new AnalysisRunDetailResponse
        {
            Summary = ToSummary(run),
            Findings = run.Findings.Select(ToFindingResponse).ToList()
        };
    }

    public async Task<RunDiffResponse?> GetDiffAsync(int baselineRunId, int currentRunId, CancellationToken cancellationToken)
    {
        var baseline = await db.AnalysisRuns.Include(r => r.Findings).FirstOrDefaultAsync(r => r.Id == baselineRunId, cancellationToken);
        var current = await db.AnalysisRuns.Include(r => r.Findings).FirstOrDefaultAsync(r => r.Id == currentRunId, cancellationToken);
        if (baseline is null || current is null)
            return null;

        static string Key(FindingEntity f) => string.Join('', f.Category, f.FilePath, f.Line, f.MemberName);

        var baselineKeys = baseline.Findings.Select(Key).ToHashSet();
        var currentKeys = current.Findings.Select(Key).ToHashSet();

        var newFindings = current.Findings.Where(f => !baselineKeys.Contains(Key(f)));
        var resolvedFindings = baseline.Findings.Where(f => !currentKeys.Contains(Key(f)));
        var persistingCount = current.Findings.Count(f => baselineKeys.Contains(Key(f)));

        return new RunDiffResponse
        {
            BaselineRunId = baselineRunId,
            CurrentRunId = currentRunId,
            NewFindings = newFindings.Select(ToFindingResponse).ToList(),
            ResolvedFindings = resolvedFindings.Select(ToFindingResponse).ToList(),
            PersistingCount = persistingCount
        };
    }

    private static FindingResponse ToFindingResponse(FindingEntity f) => new()
    {
        Category = f.Category,
        Severity = f.Severity,
        FilePath = f.FilePath,
        Line = f.Line,
        MemberName = f.MemberName,
        Message = f.Message,
        CodeSnippet = f.CodeSnippet,
        Recommendation = f.Recommendation
    };

    private static AnalysisRunSummaryResponse ToSummary(AnalysisRunEntity run) => new()
    {
        RunId = run.Id,
        TargetPath = run.TargetPath,
        StartedAtUtc = run.StartedAtUtc,
        CompletedAtUtc = run.CompletedAtUtc,
        FilesScanned = run.FilesScanned,
        DbContextsFound = run.DbContextsFound,
        EntityTypesFound = run.EntityTypesFound,
        TotalFindings = run.Findings.Count,
        SuppressedCount = run.SuppressedCount,
        FindingsByCategory = run.Findings
            .GroupBy(f => f.Category)
            .ToDictionary(g => g.Key, g => g.Count())
    };

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private static string StripPrefix(string path, string? prefix)
    {
        if (prefix is null || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return path;

        return path[prefix.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
