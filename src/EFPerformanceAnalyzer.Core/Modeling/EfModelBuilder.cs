using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFPerformanceAnalyzer.Core.Modeling;

/// <summary>
/// Builds an approximate EF Core model (DbContexts, DbSets, entities, navigation properties)
/// purely from syntax trees, without a full semantic compilation. This lets the tool scan any
/// C# codebase directly off disk, whether or not it currently builds.
/// </summary>
public static class EfModelBuilder
{
    private static readonly string[] CollectionTypeNames =
        ["ICollection", "IList", "List", "IEnumerable", "HashSet", "ISet", "IReadOnlyCollection", "IReadOnlyList"];

    public static EfModel Build(IReadOnlyList<SyntaxTree> trees)
    {
        var classInfos = CollectClassInfos(trees);

        var dbContextNames = classInfos.Values
            .Where(c => IsDbContext(c, classInfos, []))
            .Select(c => c.Name)
            .ToHashSet();

        var dbContextDbSets = new Dictionary<string, List<string>>();
        var dbSetPropertyToEntityType = new Dictionary<string, string>();
        var entityTypeNames = new HashSet<string>();

        foreach (var contextName in dbContextNames)
        {
            var classInfo = classInfos[contextName];
            var dbSetNames = new List<string>();

            foreach (var prop in classInfo.Properties)
            {
                if (prop.Type is not GenericNameSyntax { Identifier.Text: "DbSet" } generic)
                    continue;
                if (generic.TypeArgumentList.Arguments.Count != 1)
                    continue;

                var entityName = SimpleTypeName(generic.TypeArgumentList.Arguments[0]);
                if (string.IsNullOrEmpty(entityName))
                    continue;

                dbSetNames.Add(prop.Identifier.Text);
                dbSetPropertyToEntityType[prop.Identifier.Text] = entityName;
                entityTypeNames.Add(entityName);
            }

            dbContextDbSets[contextName] = dbSetNames;
        }

        // Fixed-point expansion: pull in entity types reachable via navigation properties
        // from the DbSet root types, so relations without their own DbSet are still modeled.
        var entityTypes = new Dictionary<string, EntityType>();
        var toProcess = new Queue<string>(entityTypeNames);
        var processed = new HashSet<string>();

        while (toProcess.Count > 0)
        {
            var typeName = toProcess.Dequeue();
            if (!processed.Add(typeName))
                continue;
            if (!classInfos.TryGetValue(typeName, out var classInfo))
                continue;
            if (dbContextNames.Contains(typeName))
                continue;

            var entity = new EntityType { Name = typeName };

            foreach (var prop in classInfo.Properties)
            {
                var navigation = TryBuildNavigation(prop, typeName, classInfos, dbContextNames);
                if (navigation is null)
                    continue;

                entity.NavigationProperties.Add(navigation);
                if (!processed.Contains(navigation.TargetEntityName))
                    toProcess.Enqueue(navigation.TargetEntityName);
            }

            entityTypes[typeName] = entity;
        }

        return new EfModel
        {
            DbContextDbSets = dbContextDbSets.ToDictionary(kv => kv.Key, IReadOnlyList<string> (kv) => kv.Value),
            DbSetPropertyToEntityType = dbSetPropertyToEntityType,
            EntityTypes = entityTypes
        };
    }

    private static NavigationProperty? TryBuildNavigation(
        PropertyDeclarationSyntax prop,
        string declaringEntityName,
        Dictionary<string, ClassInfo> classInfos,
        HashSet<string> dbContextNames)
    {
        var lineSpan = prop.GetLocation().GetLineSpan();
        var filePath = lineSpan.Path;
        var line = lineSpan.StartLinePosition.Line + 1;

        // Collection navigation: ICollection<Target> / List<Target> / etc.
        if (prop.Type is GenericNameSyntax { TypeArgumentList.Arguments.Count: 1 } generic &&
            CollectionTypeNames.Contains(generic.Identifier.Text))
        {
            var targetName = SimpleTypeName(generic.TypeArgumentList.Arguments[0]);
            if (IsPlausibleEntityTarget(targetName, classInfos, dbContextNames))
            {
                return new NavigationProperty
                {
                    Name = prop.Identifier.Text,
                    DeclaringEntityName = declaringEntityName,
                    TargetEntityName = targetName,
                    IsCollection = true,
                    FilePath = filePath,
                    Line = line
                };
            }
            return null;
        }

        // Reference navigation: a property whose type is another known class in the codebase.
        var simpleName = SimpleTypeName(prop.Type);
        if (IsPlausibleEntityTarget(simpleName, classInfos, dbContextNames) && simpleName != declaringEntityName)
        {
            return new NavigationProperty
            {
                Name = prop.Identifier.Text,
                DeclaringEntityName = declaringEntityName,
                TargetEntityName = simpleName,
                IsCollection = false,
                FilePath = filePath,
                Line = line
            };
        }

        return null;
    }

    private static bool IsPlausibleEntityTarget(
        string typeName, Dictionary<string, ClassInfo> classInfos, HashSet<string> dbContextNames) =>
        !string.IsNullOrEmpty(typeName) &&
        classInfos.ContainsKey(typeName) &&
        !dbContextNames.Contains(typeName);

    private static bool IsDbContext(ClassInfo classInfo, Dictionary<string, ClassInfo> classInfos, HashSet<string> visited)
    {
        if (!visited.Add(classInfo.Name))
            return false;

        foreach (var baseName in classInfo.BaseTypeNames)
        {
            if (baseName is "DbContext" or "IdentityDbContext")
                return true;
            if (classInfos.TryGetValue(baseName, out var baseInfo) && IsDbContext(baseInfo, classInfos, visited))
                return true;
        }

        return false;
    }

    private static Dictionary<string, ClassInfo> CollectClassInfos(IReadOnlyList<SyntaxTree> trees)
    {
        var result = new Dictionary<string, ClassInfo>();

        foreach (var tree in trees)
        {
            var root = tree.GetRoot();
            var typeDeclarations = root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .Where(t => t is ClassDeclarationSyntax or RecordDeclarationSyntax);

            foreach (var typeDecl in typeDeclarations)
            {
                var name = typeDecl.Identifier.Text;
                if (!result.TryGetValue(name, out var info))
                {
                    info = new ClassInfo { Name = name, FilePath = tree.FilePath };
                    result[name] = info;
                }

                info.Properties.AddRange(typeDecl.Members.OfType<PropertyDeclarationSyntax>());

                if (typeDecl.BaseList is not null)
                {
                    foreach (var baseType in typeDecl.BaseList.Types)
                    {
                        var baseName = SimpleTypeName(baseType.Type);
                        if (!string.IsNullOrEmpty(baseName))
                            info.BaseTypeNames.Add(baseName);
                    }
                }

                // Record primary-constructor parameters as properties too (positional records).
                if (typeDecl is RecordDeclarationSyntax { ParameterList: not null } record)
                {
                    foreach (var param in record.ParameterList!.Parameters)
                    {
                        // Represented separately since it's a ParameterSyntax, not PropertyDeclarationSyntax;
                        // navigation detection for positional records is intentionally out of scope for now.
                        _ = param;
                    }
                }
            }
        }

        return result;
    }

    private static string SimpleTypeName(TypeSyntax type)
    {
        var text = type switch
        {
            NullableTypeSyntax nullable => nullable.ElementType.ToString(),
            _ => type.ToString()
        };

        var lastDot = text.LastIndexOf('.');
        if (lastDot >= 0)
            text = text[(lastDot + 1)..];

        return text.Trim();
    }

    private sealed class ClassInfo
    {
        public required string Name { get; init; }
        public required string? FilePath { get; init; }
        public List<PropertyDeclarationSyntax> Properties { get; } = [];
        public List<string> BaseTypeNames { get; } = [];
    }
}
