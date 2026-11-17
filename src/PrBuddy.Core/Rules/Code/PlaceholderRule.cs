using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>PRB007: unfinished work: NotImplementedException, TODO comments, placeholder strings.</summary>
public sealed partial class PlaceholderRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB007", "Placeholder or unfinished code", Severity.Warning, "Completeness");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (!file.IsNew(node)) continue;

            // A fake or mock in a test leaves the interface members it doesn't need unimplemented on purpose.
            if (ThrowsNotImplemented(node) && !(file.IsTestFile && InTestDouble(node)))
                yield return Report(file, node, "Throws NotImplementedException: this code path isn't finished.");
            // Placeholder-looking strings are normal test data, so only check production code.
            else if (!file.IsTestFile && node is LiteralExpressionSyntax literal &&
                     literal.IsKind(SyntaxKind.StringLiteralExpression) && IsPlaceholder(literal.Token.ValueText))
                yield return Report(file, node, $"Placeholder value \"{SyntaxHelpers.Truncate(literal.Token.ValueText, 40)}\" in new code.");
        }

        foreach (var trivia in root.DescendantTrivia())
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) && !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)) continue;
            var text = trivia.ToString();
            if (file.IsNew(trivia) && TodoMarker().IsMatch(text))
                yield return Report(file.Path, AnalyzedFile.LineOf(trivia), $"Unfinished work left in a comment: {SyntaxHelpers.Truncate(text)}", Severity.Info);
        }
    }

    static bool ThrowsNotImplemented(SyntaxNode node)
    {
        var thrown = node switch
        {
            ThrowStatementSyntax t => t.Expression,
            ThrowExpressionSyntax t => t.Expression,
            _ => null,
        };
        return thrown is ObjectCreationExpressionSyntax creation &&
               creation.Type.ToString().EndsWith("NotImplementedException", StringComparison.Ordinal);
    }

    static bool InTestDouble(SyntaxNode node) =>
        node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault() is { BaseList: not null };

    public static bool IsPlaceholder(string value)
    {
        var trimmed = value.Trim();
        return ExactPlaceholder().IsMatch(trimmed) || ContainsPlaceholder().IsMatch(trimmed);
    }

    [GeneratedRegex(@"\b(TODO|FIXME|HACK|XXX)\b")]
    private static partial Regex TodoMarker();

    [GeneratedRegex(@"^(todo|tbd|fixme|changeme|change[-_ ]?me|replace[-_ ]?me|placeholder|x{3,})$", RegexOptions.IgnoreCase)]
    private static partial Regex ExactPlaceholder();

    [GeneratedRegex(@"your[-_ ]?(api[-_ ]?)?(key|token|secret|password)|your[-_ ][\w-]+[-_ ]here|lorem ipsum|<insert", RegexOptions.IgnoreCase)]
    private static partial Regex ContainsPlaceholder();
}
