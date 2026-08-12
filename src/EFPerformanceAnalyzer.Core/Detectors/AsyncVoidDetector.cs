using EFPerformanceAnalyzer.Core.Modeling;
using EFPerformanceAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFPerformanceAnalyzer.Core.Detectors;

/// <summary>
/// Flags `async void` methods. The caller gets no Task to await, so it cannot know when the work
/// finished or observe a failure — exceptions escape onto the thread pool and crash the process
/// rather than propagating. Event handlers are the one legitimate use and are excluded.
/// </summary>
public sealed class AsyncVoidDetector : IDetector
{
    public IEnumerable<Finding> Detect(IReadOnlyList<SyntaxTree> trees, EfModel model)
    {
        foreach (var tree in trees)
        {
            foreach (var method in DetectorSupport.GetMethods(tree))
            {
                if (!DetectorSupport.IsAsync(method))
                    continue;

                if (method.ReturnType is not PredefinedTypeSyntax predefined ||
                    !predefined.Keyword.IsKind(SyntaxKind.VoidKeyword))
                    continue;

                if (LooksLikeEventHandler(method))
                    continue;

                var (filePath, line) = DetectorSupport.GetLocation(method);

                yield return new Finding
                {
                    Category = FindingCategory.AsyncVoid,
                    Severity = Severity.Critical,
                    FilePath = filePath,
                    Line = line,
                    MemberName = DetectorSupport.QualifiedMethodName(method),
                    CodeSnippet = $"async void {method.Identifier.Text}{method.ParameterList}",
                    Message = "'async void' gives the caller no Task to await, so completion can't be awaited and " +
                              "exceptions can't be caught — an unhandled failure crashes the process instead of " +
                              "surfacing to the caller.",
                    Recommendation = "Return Task instead of void so callers can await the method and observe its " +
                                     "exceptions."
                };
            }
        }
    }

    private static bool LooksLikeEventHandler(MethodDeclarationSyntax method)
    {
        var parameters = method.ParameterList.Parameters;
        if (parameters.Count != 2)
            return false;

        var secondType = parameters[1].Type?.ToString() ?? string.Empty;
        return secondType.EndsWith("EventArgs", StringComparison.Ordinal);
    }
}
