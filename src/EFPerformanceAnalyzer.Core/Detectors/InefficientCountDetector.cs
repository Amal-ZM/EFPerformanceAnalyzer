using EFPerformanceAnalyzer.Core.Modeling;
using EFPerformanceAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFPerformanceAnalyzer.Core.Detectors;

/// <summary>
/// Flags existence checks written as `.Count() > 0`. Against EF this counts every matching row
/// (SELECT COUNT(*)) when the question is only "is there at least one" — which SQL answers far
/// more cheaply with EXISTS, i.e. `.Any()`.
/// </summary>
public sealed class InefficientCountDetector : IDetector
{
    public IEnumerable<Finding> Detect(IReadOnlyList<SyntaxTree> trees, EfModel model)
    {
        foreach (var tree in trees)
        {
            foreach (var method in DetectorSupport.GetMethods(tree))
            {
                foreach (var binary in method.DescendantNodes().OfType<BinaryExpressionSyntax>())
                {
                    if (!IsComparison(binary))
                        continue;

                    var countCall = AsCountCall(binary.Left) ?? AsCountCall(binary.Right);
                    if (countCall is null)
                        continue;

                    if (!IsZeroOrOneLiteral(binary.Left) && !IsZeroOrOneLiteral(binary.Right))
                        continue;

                    var onDbSet = DetectorSupport.RootsAtDbSet(countCall, model);
                    var (filePath, line) = DetectorSupport.GetLocation(binary);

                    yield return new Finding
                    {
                        Category = FindingCategory.InefficientCount,
                        Severity = onDbSet ? Severity.Warning : Severity.Info,
                        FilePath = filePath,
                        Line = line,
                        MemberName = DetectorSupport.QualifiedMethodName(method),
                        CodeSnippet = DetectorSupport.GetSnippet(binary),
                        Message = onDbSet
                            ? "An existence check is expressed as Count() compared against a constant. Against the " +
                              "database this issues SELECT COUNT(*) and counts every matching row before answering."
                            : "An existence check is expressed as Count() compared against a constant, which walks the " +
                              "whole sequence even though the answer is known at the first match.",
                        Recommendation = "Use .Any() (or !.Any()) instead — it translates to SQL EXISTS and stops at " +
                                         "the first match."
                    };
                }
            }
        }
    }

    private static bool IsComparison(BinaryExpressionSyntax binary) => binary.Kind() is
        SyntaxKind.GreaterThanExpression or SyntaxKind.GreaterThanOrEqualExpression or
        SyntaxKind.LessThanExpression or SyntaxKind.LessThanOrEqualExpression or
        SyntaxKind.EqualsExpression or SyntaxKind.NotEqualsExpression;

    private static InvocationExpressionSyntax? AsCountCall(ExpressionSyntax expression) =>
        expression is InvocationExpressionSyntax inv &&
        DetectorSupport.InvokedMemberName(inv) is "Count" or "LongCount" &&
        inv.ArgumentList.Arguments.Count == 0
            ? inv
            : null;

    private static bool IsZeroOrOneLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { Token.Value: 0 or 1 };
}
