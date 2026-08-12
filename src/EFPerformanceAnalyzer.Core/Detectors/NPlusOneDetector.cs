using EFPerformanceAnalyzer.Core.Analysis;
using EFPerformanceAnalyzer.Core.Modeling;
using EFPerformanceAnalyzer.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFPerformanceAnalyzer.Core.Detectors;

/// <summary>
/// Flags navigation-property access inside a loop over a query result, where that navigation
/// was not eagerly loaded via Include() — the classic N+1 pattern (one query per loop iteration).
/// </summary>
public sealed class NPlusOneDetector : IDetector
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
                if (analysis.ChainSources.Count == 0)
                    continue;

                foreach (var foreachStmt in method.Body.DescendantNodes().OfType<ForEachStatementSyntax>())
                {
                    var source = ResolveSource(foreachStmt, analysis);
                    if (source is null || source.IsProjected)
                        continue;

                    var loopVarName = foreachStmt.Identifier.Text;
                    var reportedNavs = new HashSet<string>();

                    foreach (var (varName, navName, node) in DetectorSupport.FindMemberAccesses(foreachStmt.Statement))
                    {
                        if (varName != loopVarName)
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
                            Category = FindingCategory.NPlusOneQuery,
                            Severity = Severity.Critical,
                            FilePath = filePath,
                            Line = line,
                            MemberName = DetectorSupport.QualifiedMethodName(method),
                            CodeSnippet = DetectorSupport.GetSnippet(node),
                            Message = $"'{loopVarName}.{navName}' is accessed inside a loop over '{source.EntityTypeName}' " +
                                      $"results, but '{navName}' was not eager-loaded with Include(). This triggers one extra " +
                                      "query per iteration (N+1).",
                            Recommendation = $"Add .Include(x => x.{navName}) to the query that produces '{loopVarName}', " +
                                              "or restructure the loop to avoid per-item navigation access."
                        };
                    }
                }
            }
        }
    }

    private static QuerySource? ResolveSource(ForEachStatementSyntax foreachStmt, MethodQueryAnalysis analysis)
    {
        foreach (var source in analysis.ChainSources)
        {
            if (source.OutermostNode.Span == foreachStmt.Expression.Span)
                return source;
        }

        if (foreachStmt.Expression is IdentifierNameSyntax idName &&
            analysis.VariableSources.TryGetValue(idName.Identifier.Text, out var varSource))
            return varSource;

        return null;
    }
}
