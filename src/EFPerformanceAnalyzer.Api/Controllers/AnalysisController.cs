using EFPerformanceAnalyzer.Api.Contracts;
using EFPerformanceAnalyzer.Api.Options;
using EFPerformanceAnalyzer.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EFPerformanceAnalyzer.Api.Controllers;

[ApiController]
[Route("api/analysis")]
public sealed class AnalysisController(AnalysisService analysisService, IOptions<ScanSettings> scanSettings) : ControllerBase
{
    /// <summary>
    /// Scans a C# codebase for EF Core performance anti-patterns: N+1 queries, missing
    /// AsNoTracking(), missing Include(), unused navigation properties, and multiple
    /// SaveChanges() calls per method. The target path must fall under a configured
    /// ScanSettings:AllowedRoots entry.
    /// </summary>
    [HttpPost("scans")]
    [ProducesResponseType(typeof(AnalysisRunSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RunScan([FromBody] ScanRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var summary = await analysisService.RunScanAsync(request.TargetPath, cancellationToken);
            return Ok(summary);
        }
        catch (ScanRejectedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Scans a project you don't have reachable on this machine's filesystem: zip it up and
    /// upload it here instead of using /scans with a targetPath. The archive is extracted to an
    /// isolated temp directory, scanned, and deleted immediately afterward — nothing from the
    /// upload is retained beyond the findings themselves.
    /// </summary>
    [HttpPost("scans/upload")]
    [RequestSizeLimit(2L * 1024 * 1024 * 1024)] // hard ceiling; ScanSettings:MaxUploadSizeBytes is the real enforced limit, checked below
    [ProducesResponseType(typeof(AnalysisRunSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RunUploadScan(IFormFile? file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "A non-empty .zip file is required (multipart form field 'file')." });

        if (!Path.GetExtension(file.FileName).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Only .zip archives are accepted." });

        if (file.Length > scanSettings.Value.MaxUploadSizeBytes)
        {
            return BadRequest(new
            {
                error = $"File is {file.Length} bytes, exceeding the {scanSettings.Value.MaxUploadSizeBytes} byte limit."
            });
        }

        try
        {
            await using var stream = file.OpenReadStream();
            var summary = await analysisService.RunUploadScanAsync(stream, file.FileName, cancellationToken);
            return Ok(summary);
        }
        catch (ScanRejectedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Scans a folder picked directly in the browser's web UI (wwwroot) via a folder picker or
    /// drag-and-drop — no zip step. Each file arrives as its own multipart part carrying its
    /// original relative path as the filename.
    /// </summary>
    [HttpPost("scans/upload-folder")]
    [RequestSizeLimit(2L * 1024 * 1024 * 1024)]
    [ProducesResponseType(typeof(AnalysisRunSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RunFolderUploadScan(
        [FromForm] List<IFormFile> files, [FromForm] string? folderName, CancellationToken cancellationToken)
    {
        try
        {
            var summary = await analysisService.RunFolderUploadScanAsync(files, folderName, cancellationToken);
            return Ok(summary);
        }
        catch (ScanRejectedException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("runs")]
    [ProducesResponseType(typeof(IReadOnlyList<AnalysisRunSummaryResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRuns(CancellationToken cancellationToken)
    {
        var runs = await analysisService.GetRunsAsync(cancellationToken);
        return Ok(runs);
    }

    [HttpGet("runs/{runId:int}")]
    [ProducesResponseType(typeof(AnalysisRunDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRunDetail(int runId, CancellationToken cancellationToken)
    {
        var detail = await analysisService.GetRunDetailAsync(runId, cancellationToken);
        return detail is null ? NotFound() : Ok(detail);
    }

    /// <summary>
    /// Exports a run's findings in a format another tool can consume: SARIF for GitHub Code
    /// Scanning / VS Code's Problems panel, CSV for a spreadsheet, or Markdown for a ticket or PR.
    /// </summary>
    [HttpGet("runs/{runId:int}/export/{format}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ExportRun(int runId, string format, CancellationToken cancellationToken)
    {
        var detail = await analysisService.GetRunDetailAsync(runId, cancellationToken);
        if (detail is null)
            return NotFound();

        return format.ToLowerInvariant() switch
        {
            "sarif" => File(System.Text.Encoding.UTF8.GetBytes(ReportExporters.ToSarif(detail)),
                "application/sarif+json", $"ef-analyzer-run-{runId}.sarif"),
            "csv" => File(System.Text.Encoding.UTF8.GetBytes(ReportExporters.ToCsv(detail)),
                "text/csv", $"ef-analyzer-run-{runId}.csv"),
            "md" or "markdown" => File(System.Text.Encoding.UTF8.GetBytes(ReportExporters.ToMarkdown(detail)),
                "text/markdown", $"ef-analyzer-run-{runId}.md"),
            _ => BadRequest(new { error = "format must be one of: sarif, csv, md" })
        };
    }

    /// <summary>
    /// Compares two runs of (presumably) the same project — what's new since the baseline, what
    /// got fixed, and what's still there. Findings are matched on (category, file, line, member)
    /// since there's no persistent finding ID across scans.
    /// </summary>
    [HttpGet("runs/{baselineRunId:int}/diff/{currentRunId:int}")]
    [ProducesResponseType(typeof(RunDiffResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDiff(int baselineRunId, int currentRunId, CancellationToken cancellationToken)
    {
        var diff = await analysisService.GetDiffAsync(baselineRunId, currentRunId, cancellationToken);
        return diff is null ? NotFound() : Ok(diff);
    }
}
