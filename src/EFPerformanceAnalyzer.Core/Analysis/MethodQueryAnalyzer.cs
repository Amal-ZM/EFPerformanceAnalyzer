using EFPerformanceAnalyzer.Core.Modeling;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFPerformanceAnalyzer.Core.Analysis;

/// <summary>
/// Walks the fluent LINQ chain hanging off each DbSet access inside a method body
/// (`_context.Students.Where(...).Include(...).ToList()`) so detectors can reason about
/// what was queried, what was included, and whether the result was tracked or materialized.
/// </summary>
public static class MethodQueryAnalyzer
{
    private static readonly HashSet<string> CollectionMaterializers =
        ["ToList", "ToListAsync", "ToArray", "ToArrayAsync", "AsEnumerable", "ToHashSet", "ToHashSetAsync"];

    private static readonly HashSet<string> SingleMaterializers =
    [
        "FirstOrDefault", "FirstOrDefaultAsync", "First", "FirstAsync",
        "SingleOrDefault", "SingleOrDefaultAsync", "Single", "SingleAsync", "Find", "FindAsync"
    ];

    public static MethodQueryAnalysis Analyze(BaseMethodDeclarationSyntax method, EfModel model)
    {
        var chainSources = new List<QuerySource>();
        var variableSources = new Dictionary<string, QuerySource>();

        if (method.Body is null && method.ExpressionBody is null)
            return new MethodQueryAnalysis { Method = method, ChainSources = chainSources, VariableSources = variableSources };

        SyntaxNode bodyNode = (SyntaxNode?)method.Body ?? method.ExpressionBody!;

        var dbSetAccesses = bodyNode.DescendantNodes()
            .OfType<MemberAccessExpressionSyntax>()
            .Where(ma => model.IsDbSetPropertyName(ma.Name.Identifier.Text))
            // Only root accesses: the object being accessed off of isn't itself a further chained call
            // (this simply avoids re-walking the same DbSet access twice; each textual `.Students` is distinct already).
            .ToList();

        foreach (var dbSetAccess in dbSetAccesses)
        {
            var entityTypeName = model.DbSetPropertyToEntityType[dbSetAccess.Name.Identifier.Text];
            var source = WalkChain(dbSetAccess, entityTypeName);
            chainSources.Add(source);

            var varName = TryGetAssignedVariableName(source.OutermostNode);
            if (varName is not null)
                variableSources[varName] = source;
        }

        return new MethodQueryAnalysis { Method = method, ChainSources = chainSources, VariableSources = variableSources };
    }

    private static QuerySource WalkChain(MemberAccessExpressionSyntax dbSetAccess, string entityTypeName)
    {
        var includedNavigations = new HashSet<string>();
        var stepNames = new List<string>();
        SyntaxNode current = dbSetAccess;

        while (true)
        {
            if (current.Parent is MemberAccessExpressionSyntax ma && ma.Expression == current)
            {
                var methodName = ma.Name.Identifier.Text;

                if (ma.Parent is InvocationExpressionSyntax inv && inv.Expression == ma)
                {
                    stepNames.Add(methodName);
                    if (methodName is "Include" or "ThenInclude")
                        CollectIncludedNavigations(inv, includedNavigations);

                    current = inv;
                    continue;
                }

                stepNames.Add(methodName);
                current = ma;
                continue;
            }

            break;
        }

        var lastStep = stepNames.Count > 0 ? stepNames[^1] : null;
        var isCollectionMaterializer = lastStep is not null && CollectionMaterializers.Contains(lastStep);
        var isSingleMaterializer = lastStep is not null && SingleMaterializers.Contains(lastStep);

        return new QuerySource
        {
            EntityTypeName = entityTypeName,
            IsCollection = !isSingleMaterializer,
            HasAsNoTracking = stepNames.Contains("AsNoTracking"),
            IsProjected = stepNames.Contains("Select"),
            IsMaterialized = isCollectionMaterializer || isSingleMaterializer,
            IncludedNavigations = includedNavigations,
            StepNames = stepNames,
            RootMemberAccess = dbSetAccess,
            OutermostNode = current
        };
    }

    private static void CollectIncludedNavigations(InvocationExpressionSyntax includeInvocation, HashSet<string> into)
    {
        if (includeInvocation.ArgumentList.Arguments.Count == 0)
            return;

        var argExpr = includeInvocation.ArgumentList.Arguments[0].Expression;

        switch (argExpr)
        {
            case SimpleLambdaExpressionSyntax or ParenthesizedLambdaExpressionSyntax:
            {
                var body = argExpr switch
                {
                    SimpleLambdaExpressionSyntax simple => (SyntaxNode)simple.Body,
                    ParenthesizedLambdaExpressionSyntax paren => paren.Body,
                    _ => argExpr
                };

                var lastAccess = body.DescendantNodesAndSelf()
                    .OfType<MemberAccessExpressionSyntax>()
                    .LastOrDefault();

                if (lastAccess is not null)
                    into.Add(lastAccess.Name.Identifier.Text);
                break;
            }
            case LiteralExpressionSyntax { Token.Value: string literal }:
            {
                foreach (var segment in literal.Split('.'))
                    into.Add(segment.Trim());
                break;
            }
        }
    }

    private static string? TryGetAssignedVariableName(SyntaxNode outermost)
    {
        if (outermost.Parent is EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax declarator })
            return declarator.Identifier.Text;

        if (outermost.Parent is AssignmentExpressionSyntax assign &&
            assign.Right == outermost &&
            assign.Left is IdentifierNameSyntax idName)
            return idName.Identifier.Text;

        return null;
    }
}
