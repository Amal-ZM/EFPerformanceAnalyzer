using EFPerformanceAnalyzer.Core.Analysis;
using EFPerformanceAnalyzer.Core.Modeling;
using EFPerformanceAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;

namespace EFPerformanceAnalyzer.Core.Detectors;

/// <summary>
/// Flags materialized queries (ToList/FirstOrDefault/etc.) in methods that never call
/// SaveChanges — a strong signal the query is read-only and change tracking is pure overhead.
/// </summary>
public sealed class MissingAsNoTrackingDetector : IDetector
{
    public IEnumerable<Finding> Detect(IReadOnlyList<SyntaxTree> trees, EfModel model)
    {
        foreach (var tree in trees)
        {
            foreach (var method in DetectorSupport.GetMethods(tree))
            {
                if (method.Body is null)
                    continue;

                var isReadOnlyMethod = !DetectorSupport.HasSaveChangesCall(method.Body);
                if (!isReadOnlyMethod)
                    continue;

                var analysis = MethodQueryAnalyzer.Analyze(method, model);

                foreach (var source in analysis.ChainSources)
                {
                    if (!source.IsMaterialized || source.HasAsNoTracking)
                        continue;

                    var (filePath, line) = DetectorSupport.GetLocation(source.RootMemberAccess);
                    yield return new Finding
                    {
                        Category = FindingCategory.MissingAsNoTracking,
                        Severity = Severity.Warning,
                        FilePath = filePath,
                        Line = line,
                        MemberName = DetectorSupport.QualifiedMethodName(method),
                        CodeSnippet = DetectorSupport.GetSnippet(source.OutermostNode),
                        Message = $"Query against '{source.EntityTypeName}' is materialized in a method that never " +
                                  "calls SaveChanges, but does not use .AsNoTracking().",
                        Recommendation = "Add .AsNoTracking() to read-only queries to skip EF Core's change-tracking " +
                                         "overhead."
                    };
                }
            }
        }
    }
}
