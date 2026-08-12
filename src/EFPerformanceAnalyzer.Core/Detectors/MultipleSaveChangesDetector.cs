using EFPerformanceAnalyzer.Core.Modeling;
using EFPerformanceAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFPerformanceAnalyzer.Core.Detectors;

/// <summary>
/// Flags methods that call SaveChanges()/SaveChangesAsync() more than once — usually a sign
/// several writes should be batched into a single transaction/unit of work instead.
/// </summary>
public sealed class MultipleSaveChangesDetector : IDetector
{
    public IEnumerable<Finding> Detect(IReadOnlyList<SyntaxTree> trees, EfModel model)
    {
        foreach (var tree in trees)
        {
            foreach (var method in DetectorSupport.GetMethods(tree))
            {
                if (method.Body is null)
                    continue;

                var calls = method.Body.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Where(inv => DetectorSupport.InvokedMemberName(inv) is "SaveChanges" or "SaveChangesAsync")
                    .ToList();

                if (calls.Count <= 1)
                    continue;

                var (filePath, line) = DetectorSupport.GetLocation(calls[0]);
                var lines = string.Join(", ", calls.Select(c => DetectorSupport.GetLocation(c).Line));

                yield return new Finding
                {
                    Category = FindingCategory.MultipleSaveChanges,
                    Severity = Severity.Warning,
                    FilePath = filePath,
                    Line = line,
                    MemberName = DetectorSupport.QualifiedMethodName(method),
                    CodeSnippet = DetectorSupport.GetSnippet(method),
                    Message = $"'{DetectorSupport.QualifiedMethodName(method)}' calls SaveChanges/SaveChangesAsync " +
                              $"{calls.Count} times (lines {lines}). Each call is a separate round trip to the database.",
                    Recommendation = "Batch the changes and call SaveChanges() once, or wrap the writes in an " +
                                      "explicit transaction if they must stay separate calls."
                };
            }
        }
    }
}
