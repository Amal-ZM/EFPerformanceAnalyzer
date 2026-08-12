using EFPerformanceAnalyzer.Core.Modeling;
using EFPerformanceAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFPerformanceAnalyzer.Core.Detectors;

/// <summary>
/// Flags navigation properties that are never dereferenced (`.NavName`) anywhere in the scanned
/// codebase — dead mapping that still costs EF Core relationship-tracking overhead.
/// </summary>
public sealed class UnusedNavigationPropertyDetector : IDetector
{
    public IEnumerable<Finding> Detect(IReadOnlyList<SyntaxTree> trees, EfModel model)
    {
        var usageCounts = new Dictionary<string, int>();

        foreach (var tree in trees)
        {
            var root = tree.GetRoot();

            foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
            {
                var name = access.Name.Identifier.Text;
                usageCounts[name] = usageCounts.GetValueOrDefault(name) + 1;
            }

            // Covers null-conditional access (`x?.Nav`), which Roslyn represents separately from
            // plain member access and would otherwise read as an unused navigation false positive.
            foreach (var binding in root.DescendantNodes().OfType<MemberBindingExpressionSyntax>())
            {
                var name = binding.Name.Identifier.Text;
                usageCounts[name] = usageCounts.GetValueOrDefault(name) + 1;
            }
        }

        foreach (var entity in model.EntityTypes.Values)
        {
            foreach (var nav in entity.NavigationProperties)
            {
                if (usageCounts.GetValueOrDefault(nav.Name) > 0)
                    continue;
                if (nav.FilePath is null)
                    continue;

                yield return new Finding
                {
                    Category = FindingCategory.UnusedNavigationProperty,
                    Severity = Severity.Info,
                    FilePath = nav.FilePath,
                    Line = nav.Line,
                    MemberName = $"{nav.DeclaringEntityName}.{nav.Name}",
                    CodeSnippet = $"{(nav.IsCollection ? $"ICollection<{nav.TargetEntityName}>" : nav.TargetEntityName)} {nav.Name}",
                    Message = $"Navigation property '{nav.DeclaringEntityName}.{nav.Name}' -> '{nav.TargetEntityName}' " +
                              "is never dereferenced anywhere in the scanned codebase.",
                    Recommendation = "Remove the navigation property if it's genuinely unused, or confirm it's " +
                                      "reserved for a planned feature."
                };
            }
        }
    }
}
