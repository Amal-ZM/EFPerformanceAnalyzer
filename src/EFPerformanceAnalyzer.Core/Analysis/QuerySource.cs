using Microsoft.CodeAnalysis;

namespace EFPerformanceAnalyzer.Core.Analysis;

/// <summary>
/// One EF query chain found in a method, rooted at a DbSet access (e.g. `_context.Students.Where(...).ToList()`).
/// </summary>
public sealed class QuerySource
{
    public required string EntityTypeName { get; init; }
    public required bool IsCollection { get; init; }
    public required bool HasAsNoTracking { get; init; }
    public required bool IsProjected { get; init; }
    public required bool IsMaterialized { get; init; }
    public required HashSet<string> IncludedNavigations { get; init; }

    /// <summary>
    /// Every fluent step invoked on the chain, in source order (e.g. ["Where", "Include", "ToList"]).
    /// Detectors use this to reason about ordering — notably whether a query operator appears
    /// *after* a materializer, which means it runs in memory rather than in SQL.
    /// </summary>
    public required IReadOnlyList<string> StepNames { get; init; }

    public required SyntaxNode RootMemberAccess { get; init; }
    public required SyntaxNode OutermostNode { get; init; }
}
