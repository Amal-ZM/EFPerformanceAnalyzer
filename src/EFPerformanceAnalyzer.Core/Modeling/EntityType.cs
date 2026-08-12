namespace EFPerformanceAnalyzer.Core.Modeling;

public sealed class EntityType
{
    public required string Name { get; init; }
    public List<NavigationProperty> NavigationProperties { get; } = [];
}

public sealed class NavigationProperty
{
    public required string Name { get; init; }
    public required string DeclaringEntityName { get; init; }
    public required string TargetEntityName { get; init; }
    public required bool IsCollection { get; init; }
    public string? FilePath { get; init; }
    public int Line { get; init; }
}
