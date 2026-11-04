using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>PRB001: a new catch block with nothing in it.</summary>
public sealed class EmptyCatchRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB001", "Empty catch block", Severity.Warning, "Error handling");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root) =>
        root.DescendantNodes()
            .OfType<CatchClauseSyntax>()
            .Where(c => file.IsNew(c) && c.Block.Statements.Count == 0)
            .Select(c => Report(file, c, "Empty catch block: exceptions here disappear without a trace."));
}
