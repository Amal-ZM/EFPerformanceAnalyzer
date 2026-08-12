using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFPerformanceAnalyzer.Core.Analysis;

public sealed class MethodQueryAnalysis
{
    public required BaseMethodDeclarationSyntax Method { get; init; }
    public required List<QuerySource> ChainSources { get; init; }
    public required Dictionary<string, QuerySource> VariableSources { get; init; }
}
