using EFPerformanceAnalyzer.Core.Modeling;
using EFPerformanceAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFPerformanceAnalyzer.Core.Detectors;

/// <summary>
/// Flags SaveChanges()/SaveChangesAsync() called inside a loop — each call is its own transaction
/// and round trip, so inserting N rows costs N round trips instead of one batched write.
/// </summary>
public sealed class SaveChangesInLoopDetector : IDetector
{
    public IEnumerable<Finding> Detect(IReadOnlyList<SyntaxTree> trees, EfModel model)
    {
        foreach (var tree in trees)
        {
            foreach (var method in DetectorSupport.GetMethods(tree))
            {
                if (method.Body is null)
                    continue;

                foreach (var invocation in method.Body.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (DetectorSupport.InvokedMemberName(invocation) is not ("SaveChanges" or "SaveChangesAsync"))
                        continue;

                    var loop = DetectorSupport.EnclosingLoop(invocation);
                    if (loop is null)
                        continue;

                    var (filePath, line) = DetectorSupport.GetLocation(invocation);
                    var loopKind = DetectorSupport.DescribeLoop(loop);

                    yield return new Finding
                    {
                        Category = FindingCategory.SaveChangesInLoop,
                        Severity = Severity.Critical,
                        FilePath = filePath,
                        Line = line,
                        MemberName = DetectorSupport.QualifiedMethodName(method),
                        CodeSnippet = DetectorSupport.GetSnippet(invocation),
                        Message = $"SaveChanges is called inside a {loopKind} loop, committing one transaction per " +
                                  "iteration instead of batching the whole set of changes into a single write.",
                        Recommendation = "Move the SaveChanges() call after the loop so EF batches every tracked " +
                                         "change into one round trip."
                    };
                }
            }
        }
    }
}
