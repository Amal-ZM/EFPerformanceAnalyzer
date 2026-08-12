namespace EFPerformanceAnalyzer.Api.Persistence;

public sealed class FindingEntity
{
    public int Id { get; set; }
    public int AnalysisRunId { get; set; }
    public AnalysisRunEntity? AnalysisRun { get; set; }

    public required string Category { get; set; }
    public required string Severity { get; set; }
    public required string FilePath { get; set; }
    public required int Line { get; set; }
    public required string MemberName { get; set; }
    public required string Message { get; set; }
    public required string CodeSnippet { get; set; }
    public string? Recommendation { get; set; }
}
