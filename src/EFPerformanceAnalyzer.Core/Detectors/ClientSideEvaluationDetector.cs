using EFPerformanceAnalyzer.Core.Analysis;
using EFPerformanceAnalyzer.Core.Modeling;
using EFPerformanceAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;

namespace EFPerformanceAnalyzer.Core.Detectors;

/// <summary>
/// Flags a query that materializes first and filters second — `.ToList().Where(...)` pulls every
/// row of the table into memory and then discards most of them in the application process, instead
/// of letting SQL do the filtering. Usually the single most expensive mistake in an EF codebase.
/// </summary>
public sealed class ClientSideEvaluationDetector : IDetector
{
    private static readonly HashSet<string> HardMaterializers =
        ["ToList", "ToListAsync", "ToArray", "ToArrayAsync", "ToHashSet", "ToHashSetAsync"];

    private static readonly HashSet<string> QueryOperators =
    [
        "Where", "OrderBy", "OrderByDescending", "ThenBy", "ThenByDescending",
        "First", "FirstOrDefault", "Single", "SingleOrDefault", "Last", "LastOrDefault",
        "Count", "LongCount", "Any", "All", "Skip", "Take",
        "Sum", "Average", "Min", "Max", "GroupBy", "Distinct"
    ];

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

                    for (var i = 0; i < steps.Count; i++)
                    {
                        var isHard = HardMaterializers.Contains(steps[i]);
                        var isSoft = steps[i] == "AsEnumerable";
                        if (!isHard && !isSoft)
                            continue;

                        var offender = FindLaterQueryOperator(steps, i);
                        if (offender is null)
                            break;

                        var (filePath, line) = DetectorSupport.GetLocation(source.OutermostNode);

                        yield return new Finding
                        {
                            Category = FindingCategory.ClientSideEvaluation,
                            Severity = isHard ? Severity.Critical : Severity.Warning,
                            FilePath = filePath,
                            Line = line,
                            MemberName = DetectorSupport.QualifiedMethodName(method),
                            CodeSnippet = DetectorSupport.GetSnippet(source.OutermostNode),
                            Message = $"'.{steps[i]}()' materializes the '{source.EntityTypeName}' query before " +
                                      $"'.{offender}()' runs, so '{offender}' is evaluated in memory instead of in SQL. " +
                                      "Every row of the table is loaded and then filtered client-side.",
                            Recommendation = $"Move '.{offender}()' before '.{steps[i]}()' so it translates into the " +
                                             "SQL WHERE/ORDER BY clause, and materialize only once the query is fully composed."
                        };

                        break;
                    }
                }
            }
        }
    }

    private static string? FindLaterQueryOperator(IReadOnlyList<string> steps, int materializerIndex)
    {
        for (var j = materializerIndex + 1; j < steps.Count; j++)
        {
            if (QueryOperators.Contains(steps[j]))
                return steps[j];
        }

        return null;
    }
}
