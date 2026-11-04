using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB001: a new catch block with nothing in it. Catching everything is a warning. Catching one
/// specific exception, filtering with `when`, or leaving a comment inside shows intent (best-effort
/// cleanup, say), so those are info.
/// </summary>
public sealed class EmptyCatchRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB001", "Empty catch block", Severity.Warning, "Error handling");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var c in root.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            if (!file.IsNew(c) || c.Block.Statements.Count > 0) continue;

            if (HasComment(c.Block))
                yield return Report(file, c, "Empty catch block, with a comment explaining it. Check the reason holds.", Severity.Info);
            else if (c.Filter is not null || !CatchesEverything(c))
                yield return Report(file, c, $"Empty catch block ignores {Caught(c)}. Fine for best-effort work, but make sure that's what this is.", Severity.Info);
            else
                yield return Report(file, c, "Empty catch block: every exception here disappears without a trace.");
        }
    }

    static bool CatchesEverything(CatchClauseSyntax c) =>
        c.Declaration is null || c.Declaration.Type.ToString() is "Exception" or "System.Exception" or "global::System.Exception";

    static string Caught(CatchClauseSyntax c)
    {
        var type = c.Declaration is { } d ? d.Type.ToString() : "every exception";
        return c.Filter is { } filter ? $"{type} when {SyntaxHelpers.Truncate(filter.FilterExpression.ToString(), 50)}" : type;
    }

    static bool HasComment(BlockSyntax block) =>
        block.DescendantTrivia().Any(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia) || t.IsKind(SyntaxKind.MultiLineCommentTrivia));
}
