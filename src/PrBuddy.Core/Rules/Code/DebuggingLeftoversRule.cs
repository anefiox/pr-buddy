using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB011: Debugger.Break() / Debugger.Launch() in new code. In a release build they stop the
/// process or pop up a "choose a debugger" dialog on whatever machine it runs on.
/// </summary>
public sealed class DebuggingLeftoversRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB011", "Debugger call left in", Severity.Warning, "Completeness");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root) =>
        root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "Break" or "Launch",
                Expression: IdentifierNameSyntax { Identifier.ValueText: "Debugger" } or MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Debugger" },
            } && file.IsNew(i))
            .Select(i => Report(file, i, $"{i} left in. It halts the program, or asks for a debugger, wherever this runs."));
}
