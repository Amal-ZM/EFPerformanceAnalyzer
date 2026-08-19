using EFPerformanceAnalyzer.Core.Modeling;
using EFPerformanceAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFPerformanceAnalyzer.Core.Detectors;

/// <summary>
/// Flags FromSqlRaw/ExecuteSqlRaw calls built from a $"..." interpolated string or string
/// concatenation — the raw variants execute that text as-is, so attacker-controlled input inside
/// it changes the query itself (SQL injection), unlike the *Interpolated overloads which
/// parameterize automatically.
/// </summary>
public sealed class RawSqlInjectionRiskDetector : IDetector
{
    private static readonly HashSet<string> RawSqlMethods = ["FromSqlRaw", "ExecuteSqlRaw", "ExecuteSqlRawAsync", "SqlQueryRaw"];

    public IEnumerable<Finding> Detect(IReadOnlyList<SyntaxTree> trees, EfModel model)
    {
        foreach (var tree in trees)
        {
            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var methodName = DetectorSupport.InvokedMemberName(invocation);
                if (methodName is null || !RawSqlMethods.Contains(methodName))
                    continue;
                if (invocation.ArgumentList.Arguments.Count == 0)
                    continue;

                var reason = ClassifyRisk(invocation.ArgumentList.Arguments[0].Expression);
                if (reason is null)
                    continue;

                var (filePath, line) = DetectorSupport.GetLocation(invocation);
                var interpolatedAlternative = methodName == "FromSqlRaw" ? "FromSqlInterpolated" : "ExecuteSqlInterpolated";

                yield return new Finding
                {
                    Category = FindingCategory.RawSqlInjectionRisk,
                    Severity = Severity.Critical,
                    FilePath = filePath,
                    Line = line,
                    MemberName = EnclosingMemberName(invocation),
                    CodeSnippet = DetectorSupport.GetSnippet(invocation),
                    Message = $"'{methodName}' builds its SQL from {reason} rather than a parameter placeholder. " +
                              "Anything embedded in that text becomes part of the query itself — attacker-controlled " +
                              "input can change the query's structure, not just its values (SQL injection).",
                    Recommendation = $"Use {interpolatedAlternative}($\"...\") instead — it parameterizes every " +
                                      "interpolated value automatically — or pass values as separate arguments " +
                                      $"(e.g. {methodName}(\"...WHERE Id = {{0}}\", id))."
                };
            }
        }
    }

    private static string? ClassifyRisk(ExpressionSyntax expr) => expr switch
    {
        InterpolatedStringExpressionSyntax { Contents: var contents } when contents.Any(c => c is InterpolationSyntax)
            => "a $\"...\" interpolated string",
        BinaryExpressionSyntax bin when bin.IsKind(SyntaxKind.AddExpression) => "string concatenation",
        _ => null
    };

    private static string EnclosingMemberName(SyntaxNode node)
    {
        var method = node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
        return method is null ? "" : DetectorSupport.QualifiedMethodName(method);
    }
}
