using EFPerformanceAnalyzer.Core.Modeling;
using EFPerformanceAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFPerformanceAnalyzer.Core.Detectors;

/// <summary>
/// Flags `.Include("NavName")` — the string-literal overload still works, but renaming the
/// navigation property no longer produces a compile error at the call site; it silently stops
/// eager-loading instead. The lambda form (`.Include(x => x.NavName)`) is refactor-safe.
/// </summary>
public sealed class StringBasedIncludeDetector : IDetector
{
    public IEnumerable<Finding> Detect(IReadOnlyList<SyntaxTree> trees, EfModel model)
    {
        foreach (var tree in trees)
        {
            foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (DetectorSupport.InvokedMemberName(invocation) != "Include")
                    continue;
                if (invocation.ArgumentList.Arguments.Count == 0)
                    continue;
                if (!DetectorSupport.RootsAtDbSet(invocation, model))
                    continue;

                if (invocation.ArgumentList.Arguments[0].Expression is not LiteralExpressionSyntax { Token.Value: string navPath })
                    continue;

                var (filePath, line) = DetectorSupport.GetLocation(invocation);
                var firstSegment = navPath.Split('.')[0].Trim();
                var method = invocation.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();

                yield return new Finding
                {
                    Category = FindingCategory.StringBasedInclude,
                    Severity = Severity.Info,
                    FilePath = filePath,
                    Line = line,
                    MemberName = method is null ? "" : DetectorSupport.QualifiedMethodName(method),
                    CodeSnippet = DetectorSupport.GetSnippet(invocation),
                    Message = $"Include(\"{navPath}\") names the navigation path as a string, so a rename won't " +
                              "be caught by the compiler here — it just quietly stops eager-loading.",
                    Recommendation = navPath.Contains('.')
                        ? $"Use the lambda form: .Include(x => x.{firstSegment}).ThenInclude(y => y.{navPath[(navPath.IndexOf('.') + 1)..].Trim()})."
                        : $"Use the lambda form: .Include(x => x.{firstSegment})."
                };
            }
        }
    }
}
