using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>PRB002: catch-everything that logs (or not) and carries on.</summary>
public sealed class SwallowedExceptionRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB002", "Catch-all without rethrow", Severity.Warning, "Error handling");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var c in root.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            // Empty catches are PRB001's job; a `when` filter means someone thought about it.
            if (!file.IsNew(c) || c.Block.Statements.Count == 0 || c.Filter is not null) continue;
            if (!CatchesEverything(c)) continue;
            if (c.Block.DescendantNodes().Any(n => n is ThrowStatementSyntax or ThrowExpressionSyntax)) continue;

            yield return Report(file, c, "Catches every exception and carries on without rethrowing. Make sure real failures aren't being hidden.");
        }
    }

    static bool CatchesEverything(CatchClauseSyntax c) =>
        c.Declaration is null || c.Declaration.Type.ToString() is "Exception" or "System.Exception" or "global::System.Exception";
}
