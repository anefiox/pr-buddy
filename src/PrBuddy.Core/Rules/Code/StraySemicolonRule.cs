using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB023: an empty statement, a lone ';'. After a block ("};") it's noise; as the body of an if,
/// while or for it silently detaches the code below from the condition.
/// </summary>
public sealed class StraySemicolonRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB023", "Stray semicolon", Severity.Info, "Code");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var empty in root.DescendantNodes().OfType<EmptyStatementSyntax>())
        {
            if (!file.IsNew(empty) || empty.Parent is LabeledStatementSyntax) continue;

            if (empty.Parent is IfStatementSyntax or ElseClauseSyntax or WhileStatementSyntax or ForStatementSyntax or ForEachStatementSyntax)
                yield return Report(file, empty,
                    "The ';' right after this condition is the whole body, so the code below runs whatever the condition says. " +
                    "Remove it, or write {} if an empty body is meant.", Severity.Warning);
            else
                yield return Report(file, empty, "Stray ';' (an empty statement). Remove it.");
        }
    }
}
