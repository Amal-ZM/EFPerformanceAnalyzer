using EFPerformanceAnalyzer.Core.Analysis;
using EFPerformanceAnalyzer.Core.Modeling;
using EFPerformanceAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFPerformanceAnalyzer.Core.Detectors;

/// <summary>
/// Flags a single query result whose navigation property is dereferenced later in the same
/// method without having been eager-loaded — a lazy-load (or null-reference) risk on a single
/// entity, distinct from the per-iteration N+1 case.
/// </summary>
public sealed class MissingIncludeDetector : IDetector
{
    public IEnumerable<Finding> Detect(IReadOnlyList<SyntaxTree> trees, EfModel model)
    {
        foreach (var tree in trees)
        {
            foreach (var method in DetectorSupport.GetMethods(tree))
            {
                if (method.Body is null)
                    continue;

                var analysis = MethodQueryAnalyzer.Analyze(method, model);

                foreach (var (variableName, source) in analysis.VariableSources)
                {
                    if (source.IsCollection || source.IsProjected)
                        continue;

                    var reportedNavs = new HashSet<string>();

                    foreach (var (varName, navName, node) in DetectorSupport.FindMemberAccesses(method.Body))
                    {
                        if (varName != variableName)
                            continue;
                        if (node.Span == source.RootMemberAccess.Span)
                            continue;
                        if (source.IncludedNavigations.Contains(navName))
                            continue;
                        if (model.FindNavigation(source.EntityTypeName, navName) is null)
                            continue;
                        if (!reportedNavs.Add(navName))
                            continue;

                        var (filePath, line) = DetectorSupport.GetLocation(node);
                        yield return new Finding
                        {
                            Category = FindingCategory.MissingInclude,
                            Severity = Severity.Warning,
                            FilePath = filePath,
                            Line = line,
                            MemberName = DetectorSupport.QualifiedMethodName(method),
                            CodeSnippet = DetectorSupport.GetSnippet(node),
                            Message = $"'{variableName}.{navName}' is accessed but the query that produced " +
                                      $"'{variableName}' ({source.EntityTypeName}) did not call .Include(x => x.{navName}).",
                            Recommendation = $"Add .Include(x => x.{navName}) to the query, or confirm lazy loading " +
                                              "is intentionally enabled for this navigation."
                        };
                    }
                }
            }
        }
    }
}
