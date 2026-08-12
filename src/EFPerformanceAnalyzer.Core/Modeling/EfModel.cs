namespace EFPerformanceAnalyzer.Core.Modeling;

public sealed class EfModel
{
    /// <summary>DbContext class name -> set of DbSet property names declared on it.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> DbContextDbSets { get; init; }

    /// <summary>DbSet property name -> entity type name it exposes (e.g. "Students" -> "Student").</summary>
    public required IReadOnlyDictionary<string, string> DbSetPropertyToEntityType { get; init; }

    /// <summary>Entity type name -> its model, including navigation properties.</summary>
    public required IReadOnlyDictionary<string, EntityType> EntityTypes { get; init; }

    public bool IsDbSetPropertyName(string name) => DbSetPropertyToEntityType.ContainsKey(name);

    public NavigationProperty? FindNavigation(string entityTypeName, string navigationName) =>
        EntityTypes.TryGetValue(entityTypeName, out var entity)
            ? entity.NavigationProperties.FirstOrDefault(n => n.Name == navigationName)
            : null;
}
