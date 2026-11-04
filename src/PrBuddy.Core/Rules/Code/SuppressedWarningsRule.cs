using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>PRB005: new code that turns compiler or analyzer warnings off instead of fixing them.</summary>
public sealed class SuppressedWarningsRule : AddedCodeRule
{
    static readonly HashSet<string> SuppressAttributes = new(StringComparer.Ordinal) { "SuppressMessage", "UnconditionalSuppressMessage" };

    public override RuleDescriptor Descriptor { get; } = new("PRB005", "Suppressed warnings", Severity.Warning, "Suppressions");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes(descendIntoTrivia: true))
        {
            if (!file.IsNew(node)) continue;
            switch (node)
            {
                case PragmaWarningDirectiveTriviaSyntax pragma when pragma.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword):
                    var codes = pragma.ErrorCodes.Count == 0
                        ? "(every warning)"
                        : string.Join(", ", pragma.ErrorCodes.Select(c => c.ToString().Trim()));
                    yield return Report(file, pragma, $"#pragma warning disable {codes}. Why is the warning wrong here?");
                    break;

                case NullableDirectiveTriviaSyntax nullable when nullable.SettingToken.IsKind(SyntaxKind.DisableKeyword):
                    yield return Report(file, nullable, "#nullable disable turns off null-safety checks for the code below it.");
                    break;

                case AttributeSyntax attribute when SuppressAttributes.Contains(SyntaxHelpers.AttributeName(attribute)):
                    yield return Report(file, attribute, $"[{SyntaxHelpers.Truncate(attribute.ToString())}] hides an analyzer warning. Check the justification.");
                    break;
            }
        }
    }
}
