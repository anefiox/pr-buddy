using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB006: lots of new null-forgiving operators (x!) in one file. One or two is normal; a pile of them
/// usually means warnings were silenced rather than handled. null! and default! are ignored as idiomatic.
/// </summary>
public sealed class NullForgivingRule(int threshold = NullForgivingRule.DefaultThreshold) : AddedCodeRule
{
    public const int DefaultThreshold = 3;

    /// <summary>How many new ! in one file before it's worth mentioning. Set with "PRB006": { "threshold": n }.</summary>
    public int Threshold { get; } = threshold;

    public override RuleDescriptor Descriptor { get; } = new("PRB006", "Many null-forgiving operators", Severity.Info, "Suppressions");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        var hits = root.DescendantNodes()
            .OfType<PostfixUnaryExpressionSyntax>()
            .Where(p => p.IsKind(SyntaxKind.SuppressNullableWarningExpression) && file.IsNew(p.OperatorToken))
            .Where(p => !p.Operand.IsKind(SyntaxKind.NullLiteralExpression) && !p.Operand.IsKind(SyntaxKind.DefaultLiteralExpression))
            .ToList();

        if (hits.Count >= Threshold)
            yield return Report(file, hits[0],
                $"{hits.Count} new null-forgiving operators (!). Each one tells the compiler 'trust me, this isn't null' without checking.");
    }
}
