using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>PRB009: `throw ex;` inside `catch (X ex)` throws the same exception but starts its stack trace here.</summary>
public sealed class RethrowResetsStackRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB009", "throw ex; loses the stack trace", Severity.Warning, "Error handling");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var c in root.DescendantNodes().OfType<CatchClauseSyntax>())
        {
            var name = c.Declaration?.Identifier.ValueText;
            if (string.IsNullOrEmpty(name)) continue;

            // Only throws that belong to this catch, not to a nested catch or a lambda inside it.
            var throws = c.Block.DescendantNodes(n => n is not (CatchClauseSyntax or AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
                .OfType<ThrowStatementSyntax>()
                .Where(t => t.Expression is IdentifierNameSyntax id && id.Identifier.ValueText == name && file.IsNew(t));

            foreach (var t in throws)
                yield return Report(file, t, $"'throw {name};' throws the same exception but resets its stack trace, hiding where it came from. Use 'throw;'.");
        }
    }
}
