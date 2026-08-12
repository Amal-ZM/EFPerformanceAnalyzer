namespace EFPerformanceAnalyzer.Core.Models;

public sealed class Finding
{
    public required FindingCategory Category { get; init; }
    public required Severity Severity { get; init; }
    public required string FilePath { get; init; }
    public required int Line { get; init; }
    public required string MemberName { get; init; }
    public required string Message { get; init; }
    public required string CodeSnippet { get; init; }
    public string? Recommendation { get; init; }
}
