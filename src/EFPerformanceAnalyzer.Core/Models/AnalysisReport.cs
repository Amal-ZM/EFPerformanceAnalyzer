namespace EFPerformanceAnalyzer.Core.Models;

public sealed class AnalysisReport
{
    public required string TargetPath { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public required int FilesScanned { get; init; }
    public required int DbContextsFound { get; init; }
    public required int EntityTypesFound { get; init; }
    public required IReadOnlyList<Finding> Findings { get; init; }
    public required int SuppressedCount { get; init; }

    public IReadOnlyDictionary<FindingCategory, int> SummaryByCategory =>
        Findings.GroupBy(f => f.Category).ToDictionary(g => g.Key, g => g.Count());
}
