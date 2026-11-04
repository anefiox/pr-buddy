using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Testing;

/// <summary>PRB103: a test newly switched off: [Fact(Skip = ...)], [Ignore], [Explicit].</summary>
public sealed class SkippedTestRule : AddedCodeRule
{
    static readonly HashSet<string> DisablingAttributes = new(StringComparer.Ordinal) { "Ignore", "Explicit" };

    public override RuleDescriptor Descriptor { get; } = new("PRB103", "Test disabled", Severity.Warning, "Tests");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var attribute in root.DescendantNodes().OfType<AttributeSyntax>())
        {
            if (!file.IsNew(attribute)) continue;
            var hasSkip = attribute.ArgumentList?.Arguments.Any(a => a.NameEquals?.Name.Identifier.Text == "Skip") == true;
            if (hasSkip || DisablingAttributes.Contains(SyntaxHelpers.AttributeName(attribute)))
                yield return Report(file, attribute,
                    $"Test disabled with [{SyntaxHelpers.Truncate(attribute.ToString(), 50)}]. Is it skipped because it fails?");
        }
    }
}
