using EFPerformanceAnalyzer.Core.Modeling;
using EFPerformanceAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFPerformanceAnalyzer.Core.Detectors;

/// <summary>
/// Flags string building via `+=` inside a loop. Strings are immutable, so each iteration
/// allocates a whole new string and copies everything accumulated so far — turning what looks
/// like linear work into quadratic allocation.
/// </summary>
public sealed class StringConcatInLoopDetector : IDetector
{
    public IEnumerable<Finding> Detect(IReadOnlyList<SyntaxTree> trees, EfModel model)
    {
        foreach (var tree in trees)
        {
            foreach (var method in DetectorSupport.GetMethods(tree))
            {
                foreach (var assignment in method.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                {
                    if (!assignment.IsKind(SyntaxKind.AddAssignmentExpression))
                        continue;

                    if (assignment.Left is not IdentifierNameSyntax target)
                        continue;

                    if (!IsStringValued(assignment.Right))
                        continue;

                    var loop = DetectorSupport.EnclosingLoop(assignment);
                    if (loop is null)
                        continue;

                    var (filePath, line) = DetectorSupport.GetLocation(assignment);
                    var loopKind = DetectorSupport.DescribeLoop(loop);

                    yield return new Finding
                    {
                        Category = FindingCategory.StringConcatInLoop,
                        Severity = Severity.Warning,
                        FilePath = filePath,
                        Line = line,
                        MemberName = DetectorSupport.QualifiedMethodName(method),
                        CodeSnippet = DetectorSupport.GetSnippet(assignment),
                        Message = $"'{target.Identifier.Text}' is grown with '+=' inside a {loopKind} loop. Because " +
                                  "strings are immutable, every iteration allocates a new string and copies the entire " +
                                  "accumulated value, making the loop quadratic in total work.",
                        Recommendation = "Accumulate into a StringBuilder and call ToString() once after the loop, or " +
                                         "build a collection and use string.Join()."
                    };
                }
            }
        }
    }

    /// <summary>
    /// Requires visible evidence the right-hand side is a string, so numeric `+=` accumulators —
    /// which are perfectly fine in a loop — don't get swept up.
    /// </summary>
    private static bool IsStringValued(ExpressionSyntax expression) => expression switch
    {
        LiteralExpressionSyntax literal => literal.IsKind(SyntaxKind.StringLiteralExpression),
        InterpolatedStringExpressionSyntax => true,
        BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression) =>
            IsStringValued(binary.Left) || IsStringValued(binary.Right),
        InvocationExpressionSyntax inv => DetectorSupport.InvokedMemberName(inv) == "ToString",
        _ => false
    };
}
