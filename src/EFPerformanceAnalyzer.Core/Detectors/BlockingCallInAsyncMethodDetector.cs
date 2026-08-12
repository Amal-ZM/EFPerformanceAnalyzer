using EFPerformanceAnalyzer.Core.Modeling;
using EFPerformanceAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFPerformanceAnalyzer.Core.Detectors;

/// <summary>
/// Flags Thread.Sleep inside an async method. Sleeping blocks the thread rather than yielding it,
/// which defeats the point of the method being async — the thread sits idle instead of serving
/// other requests.
/// </summary>
public sealed class BlockingCallInAsyncMethodDetector : IDetector
{
    public IEnumerable<Finding> Detect(IReadOnlyList<SyntaxTree> trees, EfModel model)
    {
        foreach (var tree in trees)
        {
            foreach (var method in DetectorSupport.GetMethods(tree))
            {
                if (!DetectorSupport.IsAsync(method))
                    continue;

                foreach (var invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (invocation.Expression is not MemberAccessExpressionSyntax
                        {
                            Name.Identifier.Text: "Sleep",
                            Expression: IdentifierNameSyntax { Identifier.Text: "Thread" }
                        })
                        continue;

                    var (filePath, line) = DetectorSupport.GetLocation(invocation);

                    yield return new Finding
                    {
                        Category = FindingCategory.BlockingCallInAsyncMethod,
                        Severity = Severity.Warning,
                        FilePath = filePath,
                        Line = line,
                        MemberName = DetectorSupport.QualifiedMethodName(method),
                        CodeSnippet = DetectorSupport.GetSnippet(invocation),
                        Message = "Thread.Sleep blocks the thread inside an async method, so the thread is held idle " +
                                  "instead of being returned to the pool to serve other work.",
                        Recommendation = "Use 'await Task.Delay(...)' instead, which yields the thread for the duration " +
                                         "of the wait."
                    };
                }
            }
        }
    }
}
