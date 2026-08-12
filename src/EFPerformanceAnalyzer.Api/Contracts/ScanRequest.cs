namespace EFPerformanceAnalyzer.Api.Contracts;

public sealed class ScanRequest
{
    public required string TargetPath { get; init; }
}
