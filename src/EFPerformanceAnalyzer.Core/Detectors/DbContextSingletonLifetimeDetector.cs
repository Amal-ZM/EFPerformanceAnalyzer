using EFPerformanceAnalyzer.Core.Modeling;
using EFPerformanceAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFPerformanceAnalyzer.Core.Detectors;

/// <summary>
/// Flags a DbContext registered with AddSingleton. DbContext is not thread-safe and isn't meant to
/// outlive a single request/scope — a shared instance across concurrent requests causes
/// "a second operation was started on this context" exceptions and cross-request data bleed under
/// real load, the kind of bug that doesn't show up until production traffic.
/// </summary>
public sealed class DbContextSingletonLifetimeDetector : IDetector
{
    public IEnumerable<Finding> Detect(IReadOnlyList<SyntaxTree> trees, EfModel model)
    {
        if (model.DbContextDbSets.Count == 0)
            yield break;

        var dbContextNames = model.DbContextDbSets.Keys.ToHashSet();

        foreach (var tree in trees)
        {
            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (invocation.Expression is not MemberAccessExpressionSyntax { Name: var name } )
                    continue;

                var methodSimpleName = name switch
                {
                    GenericNameSyntax g => g.Identifier.Text,
                    IdentifierNameSyntax id => id.Identifier.Text,
                    _ => null
                };
                if (methodSimpleName != "AddSingleton")
                    continue;

                var offendingType = FindDbContextTypeArgument(name, invocation, dbContextNames);
                if (offendingType is null)
                    continue;

                var (filePath, line) = DetectorSupport.GetLocation(invocation);
                yield return new Finding
                {
                    Category = FindingCategory.DbContextSingletonLifetime,
                    Severity = Severity.Critical,
                    FilePath = filePath,
                    Line = line,
                    MemberName = "",
                    CodeSnippet = DetectorSupport.GetSnippet(invocation),
                    Message = $"'{offendingType}' is registered with AddSingleton. DbContext instances aren't " +
                              "thread-safe and aren't meant to outlive one request — sharing one across every " +
                              "request causes concurrency exceptions and stale tracked entities under load.",
                    Recommendation = $"Register it with AddDbContext<{offendingType}>(...) (scoped by default), " +
                                      "or AddScoped explicitly if it's wrapped behind a repository/service."
                };
            }
        }
    }

    private static string? FindDbContextTypeArgument(
        SimpleNameSyntax name, InvocationExpressionSyntax invocation, HashSet<string> dbContextNames)
    {
        if (name is GenericNameSyntax generic)
        {
            foreach (var typeArg in generic.TypeArgumentList.Arguments)
            {
                var simple = SimpleTypeName(typeArg);
                if (dbContextNames.Contains(simple))
                    return simple;
            }
        }

        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            if (arg.Expression is TypeOfExpressionSyntax typeOf)
            {
                var simple = SimpleTypeName(typeOf.Type);
                if (dbContextNames.Contains(simple))
                    return simple;
            }
        }

        return null;
    }

    private static string SimpleTypeName(TypeSyntax type)
    {
        var text = type.ToString();
        var lastDot = text.LastIndexOf('.');
        return lastDot >= 0 ? text[(lastDot + 1)..] : text;
    }
}
