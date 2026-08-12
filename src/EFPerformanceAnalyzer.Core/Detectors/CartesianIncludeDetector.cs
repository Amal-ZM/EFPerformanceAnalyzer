using EFPerformanceAnalyzer.Core.Analysis;
using EFPerformanceAnalyzer.Core.Modeling;
using EFPerformanceAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;

namespace EFPerformanceAnalyzer.Core.Detectors;

/// <summary>
/// Flags a query stacking several Include()/ThenInclude() calls without AsSplitQuery(). EF turns
/// these into one JOIN-ed statement, so rows multiply across the included collections — the
/// "cartesian explosion" that makes a query returning a handful of entities transfer thousands of
/// duplicated rows.
/// </summary>
public sealed class CartesianIncludeDetector : IDetector
{
    private const int IncludeThreshold = 3;

    public IEnumerable<Finding> Detect(IReadOnlyList<SyntaxTree> trees, EfModel model)
    {
        foreach (var tree in trees)
        {
            foreach (var method in DetectorSupport.GetMethods(tree))
            {
                var analysis = MethodQueryAnalyzer.Analyze(method, model);

                foreach (var source in analysis.ChainSources)
                {
                    var includeCount = source.StepNames.Count(s => s is "Include" or "ThenInclude");
                    if (includeCount < IncludeThreshold)
                        continue;

                    if (source.StepNames.Contains("AsSplitQuery"))
                        continue;

                    var (filePath, line) = DetectorSupport.GetLocation(source.OutermostNode);

                    yield return new Finding
                    {
                        Category = FindingCategory.CartesianInclude,
                        Severity = Severity.Warning,
                        FilePath = filePath,
                        Line = line,
                        MemberName = DetectorSupport.QualifiedMethodName(method),
                        CodeSnippet = DetectorSupport.GetSnippet(source.OutermostNode),
                        Message = $"The '{source.EntityTypeName}' query chains {includeCount} Include/ThenInclude calls " +
                                  "into a single JOIN-ed SQL statement. Rows multiply across each included collection, " +
                                  "so the result set can be far larger than the entity count suggests.",
                        Recommendation = "Add .AsSplitQuery() so EF issues one query per included collection, or split " +
                                         "the load into separate queries and stitch the results together."
                    };
                }
            }
        }
    }
}
