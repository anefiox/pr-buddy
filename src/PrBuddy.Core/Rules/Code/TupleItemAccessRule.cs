using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB030: new code that reads a tuple through .Item1, .Item2 and so on. The reader has to look up
/// what each position means; deconstruction or named elements say it at the point of use.
/// </summary>
public sealed partial class TupleItemAccessRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB030", "Tuple read by position", Severity.Info, "Code");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        // One finding per method: the fix is the same for every ItemN in it.
        foreach (var group in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>()
                     .Where(m => ItemN().IsMatch(m.Name.Identifier.ValueText) && file.IsNew(m))
                     .GroupBy(m => m.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or PropertyDeclarationSyntax or LocalFunctionStatementSyntax)))
        {
            var first = group.First();
            var items = group.Select(m => m.Name.Identifier.ValueText).Distinct().Order(StringComparer.Ordinal).ToArray();
            yield return Report(file, first,
                $"'{SyntaxHelpers.Truncate(first.ToString(), 50)}' reads a tuple by position ({string.Join(", ", items)}), " +
                "so the reader has to look up what each item means. Deconstruct it (var (orgId, userId, _) = ...) " +
                "or give the tuple's elements names.");
        }
    }

    [GeneratedRegex(@"^Item[1-7]$")]
    private static partial Regex ItemN();
}
