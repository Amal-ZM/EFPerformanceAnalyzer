using EFPerformanceAnalyzer.Core.Modeling;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace EFPerformanceAnalyzer.Core.Detectors;

internal static class DetectorSupport
{
    /// <summary>
    /// The nearest enclosing loop statement, or null if <paramref name="node"/> isn't in one.
    /// Stops at the method boundary so a loop in a *calling* method never counts.
    /// </summary>
    public static SyntaxNode? EnclosingLoop(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case ForStatementSyntax:
                case ForEachStatementSyntax:
                case ForEachVariableStatementSyntax:
                case WhileStatementSyntax:
                case DoStatementSyntax:
                    return ancestor;
                case MethodDeclarationSyntax:
                case LocalFunctionStatementSyntax:
                    return null;
            }
        }

        return null;
    }

    public static string DescribeLoop(SyntaxNode loop) => loop switch
    {
        ForEachStatementSyntax or ForEachVariableStatementSyntax => "foreach",
        ForStatementSyntax => "for",
        WhileStatementSyntax => "while",
        DoStatementSyntax => "do/while",
        _ => "loop"
    };

    /// <summary>
    /// True when the left-most receiver of this expression chain is a DbSet property access —
    /// i.e. the chain is an EF query, not LINQ over an in-memory collection. Without this check
    /// the query-shape detectors would fire on ordinary <c>List&lt;T&gt;</c> code, where the same
    /// patterns are harmless.
    /// </summary>
    public static bool RootsAtDbSet(SyntaxNode node, EfModel model)
    {
        var current = node;

        while (true)
        {
            switch (current)
            {
                case InvocationExpressionSyntax inv:
                    current = inv.Expression;
                    continue;
                case MemberAccessExpressionSyntax ma:
                    if (model.IsDbSetPropertyName(ma.Name.Identifier.Text))
                        return true;
                    current = ma.Expression;
                    continue;
                case ConditionalAccessExpressionSyntax cond:
                    current = cond.Expression;
                    continue;
                case ParenthesizedExpressionSyntax paren:
                    current = paren.Expression;
                    continue;
                case AwaitExpressionSyntax awaited:
                    current = awaited.Expression;
                    continue;
                default:
                    return false;
            }
        }
    }

    public static bool IsAsync(MethodDeclarationSyntax method) =>
        method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword));

    public static IEnumerable<MethodDeclarationSyntax> GetMethods(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>();

    public static (string FilePath, int Line) GetLocation(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return (span.Path, span.StartLinePosition.Line + 1);
    }

    public static string GetSnippet(SyntaxNode node)
    {
        var text = node.ToString();
        var singleLine = text.Split('\n')[0].Trim();
        return singleLine.Length > 160 ? singleLine[..160] + "..." : singleLine;
    }

    public static string QualifiedMethodName(MethodDeclarationSyntax method)
    {
        var className = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;
        return className is null ? method.Identifier.Text : $"{className}.{method.Identifier.Text}";
    }

    public static bool HasSaveChangesCall(SyntaxNode methodBody) =>
        methodBody.DescendantNodes()
            .OfType<InvocationExpressionSyntax>()
            .Any(inv => InvokedMemberName(inv) is "SaveChanges" or "SaveChangesAsync");

    public static string? InvokedMemberName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        IdentifierNameSyntax id => id.Identifier.Text,
        _ => null
    };

    /// <summary>
    /// Finds every "identifier.Member" style access under <paramref name="root"/>, covering both
    /// plain member access (`x.Nav`) and null-conditional access (`x?.Nav`), which Roslyn represents
    /// with an unrelated node type (ConditionalAccessExpressionSyntax + MemberBindingExpressionSyntax).
    /// </summary>
    public static IEnumerable<(string VarName, string MemberName, SyntaxNode Node)> FindMemberAccesses(SyntaxNode root)
    {
        foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (access.Expression is IdentifierNameSyntax id)
                yield return (id.Identifier.Text, access.Name.Identifier.Text, access);
        }

        foreach (var conditional in root.DescendantNodes().OfType<ConditionalAccessExpressionSyntax>())
        {
            if (conditional.Expression is not IdentifierNameSyntax id)
                continue;

            var firstBinding = conditional.WhenNotNull.DescendantNodesAndSelf()
                .OfType<MemberBindingExpressionSyntax>()
                .FirstOrDefault();

            if (firstBinding is not null)
                yield return (id.Identifier.Text, firstBinding.Name.Identifier.Text, conditional);
        }
    }
}
