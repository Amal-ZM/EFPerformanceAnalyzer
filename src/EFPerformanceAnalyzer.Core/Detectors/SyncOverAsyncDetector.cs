using EFPerformanceAnalyzer.Core.Modeling;
using EFPerformanceAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFPerformanceAnalyzer.Core.Detectors;

/// <summary>
/// Flags blocking on an asynchronous call — `.Result`, `.Wait()`, `.GetAwaiter().GetResult()`.
/// Each one parks a thread-pool thread until the operation completes; under load this starves the
/// pool, and in a context with a synchronization context it deadlocks outright.
/// </summary>
public sealed class SyncOverAsyncDetector : IDetector
{
    public IEnumerable<Finding> Detect(IReadOnlyList<SyntaxTree> trees, EfModel model)
    {
        foreach (var tree in trees)
        {
            foreach (var method in DetectorSupport.GetMethods(tree))
            {
                foreach (var node in method.DescendantNodes())
                {
                    var (blocked, description) = Classify(node);
                    if (!blocked)
                        continue;

                    var (filePath, line) = DetectorSupport.GetLocation(node);

                    yield return new Finding
                    {
                        Category = FindingCategory.SyncOverAsync,
                        Severity = Severity.Critical,
                        FilePath = filePath,
                        Line = line,
                        MemberName = DetectorSupport.QualifiedMethodName(method),
                        CodeSnippet = DetectorSupport.GetSnippet(node),
                        Message = $"{description} blocks the calling thread until the asynchronous operation finishes, " +
                                  "holding a thread-pool thread hostage for the whole wait and risking deadlock where a " +
                                  "synchronization context is present.",
                        Recommendation = "Await the call instead, making the enclosing method async all the way up to " +
                                         "its entry point."
                    };
                }
            }
        }
    }

    /// <summary>
    /// Only fires where the receiver is recognisably task-shaped. `.Result` in particular is a
    /// common property name on ordinary DTOs, so blindly matching it would bury real findings in
    /// noise — precision matters more than catching every last case here.
    /// </summary>
    private static (bool Blocked, string Description) Classify(SyntaxNode node)
    {
        switch (node)
        {
            case MemberAccessExpressionSyntax { Name.Identifier.Text: "Result" } access
                when IsTaskLike(access.Expression):
                return (true, "Reading '.Result'");

            case InvocationExpressionSyntax invocation
                when DetectorSupport.InvokedMemberName(invocation) == "Wait" &&
                     invocation.ArgumentList.Arguments.Count == 0 &&
                     invocation.Expression is MemberAccessExpressionSyntax waitAccess &&
                     IsTaskLike(waitAccess.Expression):
                return (true, "Calling '.Wait()'");

            case InvocationExpressionSyntax invocation
                when DetectorSupport.InvokedMemberName(invocation) == "GetResult" &&
                     invocation.Expression is MemberAccessExpressionSyntax getResultAccess &&
                     getResultAccess.Expression is InvocationExpressionSyntax awaiter &&
                     DetectorSupport.InvokedMemberName(awaiter) == "GetAwaiter":
                return (true, "Calling '.GetAwaiter().GetResult()'");

            default:
                return (false, string.Empty);
        }
    }

    private static bool IsTaskLike(ExpressionSyntax expression) => expression switch
    {
        InvocationExpressionSyntax inv =>
            DetectorSupport.InvokedMemberName(inv)?.EndsWith("Async", StringComparison.Ordinal) == true,
        IdentifierNameSyntax id =>
            id.Identifier.Text.Contains("task", StringComparison.OrdinalIgnoreCase),
        MemberAccessExpressionSyntax ma =>
            ma.Name.Identifier.Text.Contains("task", StringComparison.OrdinalIgnoreCase),
        _ => false
    };
}
