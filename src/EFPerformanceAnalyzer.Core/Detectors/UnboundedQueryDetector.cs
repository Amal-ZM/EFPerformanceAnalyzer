using EFPerformanceAnalyzer.Core.Analysis;
using EFPerformanceAnalyzer.Core.Modeling;
using EFPerformanceAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;

namespace EFPerformanceAnalyzer.Core.Detectors;

/// <summary>
/// Flags a query that loads an entire table: materialized to a collection with no filter and no
/// paging. Harmless on a 20-row lookup table, quietly fatal on one that grows — which is exactly
/// why it's worth surfacing before the table gets big.
/// </summary>
public sealed class UnboundedQueryDetector : IDetector
{
    private static readonly HashSet<string> CollectionMaterializers =
        ["ToList", "ToListAsync", "ToArray", "ToArrayAsync", "ToHashSet", "ToHashSetAsync"];

    private static readonly HashSet<string> BoundingOperators = ["Where", "Take", "Skip", "TakeWhile", "Find", "FindAsync"];

    public IEnumerable<Finding> Detect(IReadOnlyList<SyntaxTree> trees, EfModel model)
    {
        foreach (var tree in trees)
        {
            foreach (var method in DetectorSupport.GetMethods(tree))
            {
                var analysis = MethodQueryAnalyzer.Analyze(method, model);

                foreach (var source in analysis.ChainSources)
                {
                    var steps = source.StepNames;
                    if (steps.Count == 0)
                        continue;

                    if (!CollectionMaterializers.Contains(steps[^1]))
                        continue;

                    if (steps.Any(BoundingOperators.Contains))
                        continue;

                    var (filePath, line) = DetectorSupport.GetLocation(source.OutermostNode);

                    yield return new Finding
                    {
                        Category = FindingCategory.UnboundedQuery,
                        Severity = Severity.Warning,
                        FilePath = filePath,
                        Line = line,
                        MemberName = DetectorSupport.QualifiedMethodName(method),
                        CodeSnippet = DetectorSupport.GetSnippet(source.OutermostNode),
                        Message = $"Every row of '{source.EntityTypeName}' is loaded — the query has no Where filter " +
                                  "and no Skip/Take paging, so its cost grows linearly with the table.",
                        Recommendation = "Add a Where(...) filter, or page the results with Skip()/Take(), so the " +
                                         "query cost stays bounded as the table grows."
                    };
                }
            }
        }
    }
}
