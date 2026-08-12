namespace EFPerformanceAnalyzer.Api.Persistence;

public sealed class AnalysisRunEntity
{
    public int Id { get; set; }
    public required string TargetPath { get; set; }
    public required DateTimeOffset StartedAtUtc { get; set; }
    public required DateTimeOffset CompletedAtUtc { get; set; }
    public required int FilesScanned { get; set; }
    public required int DbContextsFound { get; set; }
    public required int EntityTypesFound { get; set; }
    public required int SuppressedCount { get; set; }

    public List<FindingEntity> Findings { get; set; } = [];
}
