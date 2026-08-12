using EFPerformanceAnalyzer.Core.Analysis;
using EFPerformanceAnalyzer.Core.Modeling;
using EFPerformanceAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;

namespace EFPerformanceAnalyzer.Core.Detectors;

/// <summary>
/// Flags a database query executed inside a loop body. This is the explicit sibling of
/// <see cref="NPlusOneDetector"/>: rather than a navigation property lazily firing a query per
/// iteration, the code visibly issues a fresh round trip each time round the loop.
/// </summary>
public sealed class QueryInLoopDetector : IDetector
{
    public IEnumerable<Finding> Detect(IReadOnlyList<SyntaxTree> trees, EfModel model)
    {
        foreach (var tree in trees)
        {
            foreach (var method in DetectorSupport.GetMethods(tree))
            {
                var analysis = MethodQueryAnalyzer.Analyze(method, model);

                foreach (var source in analysis.ChainSources)
                {
                    if (!source.IsMaterialized)
                        continue;

                    var loop = DetectorSupport.EnclosingLoop(source.OutermostNode);
                    if (loop is null)
                        continue;

                    var (filePath, line) = DetectorSupport.GetLocation(source.OutermostNode);
                    var loopKind = DetectorSupport.DescribeLoop(loop);

                    yield return new Finding
                    {
                        Category = FindingCategory.QueryInLoop,
                        Severity = Severity.Critical,
                        FilePath = filePath,
                        Line = line,
                        MemberName = DetectorSupport.QualifiedMethodName(method),
                        CodeSnippet = DetectorSupport.GetSnippet(source.OutermostNode),
                        Message = $"A '{source.EntityTypeName}' query is executed inside a {loopKind} loop, " +
                                  "so the database is hit once per iteration rather than once in total.",
                        Recommendation = "Pull the query out of the loop: fetch every row you need up front with a " +
                                         "single query (e.g. a Where(x => ids.Contains(x.Id)) over the whole batch), " +
                                         "then look results up in memory inside the loop."
                    };
                }
            }
        }
    }
}
