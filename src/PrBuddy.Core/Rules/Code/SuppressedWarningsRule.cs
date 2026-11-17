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
        // [assembly: SuppressMessage] lines (GlobalSuppressions.cs) come in batches: report them together.
        var assemblyWide = new List<AttributeSyntax>();
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

                case AttributeSyntax attribute when SuppressAttributes.Contains(SyntaxHelpers.AttributeName(attribute)) &&
                    attribute.Parent is AttributeListSyntax { Target.Identifier.ValueText: "assembly" or "module" }:
                    assemblyWide.Add(attribute);
                    break;

                case AttributeSyntax attribute when SuppressAttributes.Contains(SyntaxHelpers.AttributeName(attribute)):
                    yield return Report(file, attribute, $"[{SyntaxHelpers.Truncate(attribute.ToString())}] hides an analyzer warning. Check the justification.");
                    break;
            }
        }

        if (assemblyWide.Count == 1)
            yield return Report(file, assemblyWide[0], $"[{SyntaxHelpers.Truncate(assemblyWide[0].ToString())}] hides an analyzer warning for the whole assembly. Check the justification.");
        else if (assemblyWide.Count > 1)
        {
            var ids = assemblyWide.Select(RuleId).Where(id => id is not null).Distinct().ToArray();
            yield return Report(file, assemblyWide[0],
                $"{assemblyWide.Count} assembly-wide [SuppressMessage] attributes hide analyzer rules for the whole project" +
                (ids.Length > 0 ? $": {string.Join(", ", ids.Take(8))}{(ids.Length > 8 ? ", ..." : "")}" : "") +
                ". Check each justification, or turn the rules off in .editorconfig where the choice is visible.");
        }
    }

    /// <summary>"SA1101:PrefixLocalCallsWithThis" → SA1101.</summary>
    static string? RuleId(AttributeSyntax attribute) =>
        attribute.ArgumentList?.Arguments.Skip(1).FirstOrDefault()?.Expression is LiteralExpressionSyntax literal
            ? literal.Token.ValueText.Split(':')[0].Trim()
            : null;
}
