namespace EFPerformanceAnalyzer.Api.Contracts;

public sealed class AnalysisRunSummaryResponse
{
    public required int RunId { get; init; }
    public required string TargetPath { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public required int FilesScanned { get; init; }
    public required int DbContextsFound { get; init; }
    public required int EntityTypesFound { get; init; }
    public required int TotalFindings { get; init; }
    public required int SuppressedCount { get; init; }
    public required IReadOnlyDictionary<string, int> FindingsByCategory { get; init; }
}

public sealed class FindingResponse
{
    public required string Category { get; init; }
    public required string Severity { get; init; }
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required string MemberName { get; init; }
    public required string Message { get; init; }
    public required string CodeSnippet { get; init; }
    public string? Recommendation { get; init; }
}

public sealed class AnalysisRunDetailResponse
{
    public required AnalysisRunSummaryResponse Summary { get; init; }
    public required IReadOnlyList<FindingResponse> Findings { get; init; }
}

/// <summary>
/// Compares two runs by matching findings on (category, file, line, member) — the closest thing
/// to a stable identity a heuristic static analyzer can offer, since findings have no persistent ID
/// of their own across scans.
/// </summary>
public sealed class RunDiffResponse
{
    public required int BaselineRunId { get; init; }
    public required int CurrentRunId { get; init; }
    public required IReadOnlyList<FindingResponse> NewFindings { get; init; }
    public required IReadOnlyList<FindingResponse> ResolvedFindings { get; init; }
    public required int PersistingCount { get; init; }
}
