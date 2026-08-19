using EFPerformanceAnalyzer.Core.Modeling;
using EFPerformanceAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFPerformanceAnalyzer.Core.Detectors;

/// <summary>
/// Flags an async EF Core call inside a method that already has a CancellationToken parameter
/// available but doesn't forward it. Without the token reaching the database call, cancelling the
/// request (client disconnect, timeout) doesn't actually stop the in-flight query.
/// </summary>
public sealed class MissingCancellationTokenDetector : IDetector
{
    private static readonly HashSet<string> AsyncEfMethods =
    [
        "ToListAsync", "ToArrayAsync", "FirstAsync", "FirstOrDefaultAsync", "SingleAsync", "SingleOrDefaultAsync",
        "CountAsync", "LongCountAsync", "AnyAsync", "AllAsync", "SumAsync", "AverageAsync", "MinAsync", "MaxAsync",
        "ToDictionaryAsync", "ToHashSetAsync", "SaveChangesAsync", "ExecuteSqlRawAsync", "ExecuteSqlInterpolatedAsync",
        "ExecuteUpdateAsync", "ExecuteDeleteAsync", "FindAsync"
    ];

    public IEnumerable<Finding> Detect(IReadOnlyList<SyntaxTree> trees, EfModel model)
    {
        foreach (var tree in trees)
        {
            foreach (var method in DetectorSupport.GetMethods(tree))
            {
                if (method.Body is null)
                    continue;

                var ctParam = method.ParameterList.Parameters
                    .FirstOrDefault(p => p.Type is not null && p.Type.ToString().EndsWith("CancellationToken", StringComparison.Ordinal));
                if (ctParam is null)
                    continue;

                var ctName = ctParam.Identifier.Text;

                foreach (var invocation in method.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var name = DetectorSupport.InvokedMemberName(invocation);
                    if (name is null || !AsyncEfMethods.Contains(name))
                        continue;
                    if (name != "SaveChangesAsync" && !DetectorSupport.RootsAtDbSet(invocation, model))
                        continue;

                    var passesToken = invocation.ArgumentList.Arguments
                        .Any(a => a.Expression is IdentifierNameSyntax id && id.Identifier.Text == ctName);
                    if (passesToken)
                        continue;

                    var (filePath, line) = DetectorSupport.GetLocation(invocation);
                    yield return new Finding
                    {
                        Category = FindingCategory.MissingCancellationToken,
                        Severity = Severity.Warning,
                        FilePath = filePath,
                        Line = line,
                        MemberName = DetectorSupport.QualifiedMethodName(method),
                        CodeSnippet = DetectorSupport.GetSnippet(invocation),
                        Message = $"'{name}' doesn't forward the '{ctName}' parameter this method already has, " +
                                  "so cancelling the request won't stop this database call.",
                        Recommendation = $"Pass it through: .{name}({ctName})."
                    };
                }
            }
        }
    }
}
