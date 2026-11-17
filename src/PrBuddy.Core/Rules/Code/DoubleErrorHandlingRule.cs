using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB027: an inner catch calls the same error handler as the catch-all around it. If the handler
/// throws (many do, depending on options), the outer catch handles the error a second time and the
/// caller gets it wrapped twice.
/// </summary>
public sealed class DoubleErrorHandlingRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB027", "Error handled twice", Severity.Warning, "Code");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var outer in root.DescendantNodes().OfType<TryStatementSyntax>())
        {
            var outerHandlers = outer.Catches.Where(CatchesEverything).SelectMany(c => HandlerCalls(c.Block))
                .Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
            if (outerHandlers.Count == 0) continue;

            foreach (var inner in outer.Block.DescendantNodes(LocalSemantics.DescendInto).OfType<TryStatementSyntax>())
            foreach (var innerCatch in inner.Catches)
            {
                var call = HandlerCalls(innerCatch.Block).FirstOrDefault(c => outerHandlers.Contains(c.Name));
                if (call.Call is null || !file.IsNew(call.Call)) continue;

                yield return Report(file, call.Call,
                    $"This catch calls '{call.Name}', and so does the catch around it (line {AnalyzedFile.LineOf(outer.Catches.First(CatchesEverything))}). " +
                    $"If '{call.Name}' throws, the outer catch handles the error again and wraps it twice. " +
                    "Throw from here and let the outer catch handle it once.");
                break;
            }
        }
    }

    static bool CatchesEverything(CatchClauseSyntax clause) =>
        clause.Filter is null && (clause.Declaration is null ||
            clause.Declaration.Type.ToString() is "Exception" or "System.Exception");

    /// <summary>Calls that take the caught exception, or are made on it: Handle(ex), ex.Handle(options), new X(.., ex).Handle(..).</summary>
    static IEnumerable<(InvocationExpressionSyntax Call, string Name)> HandlerCalls(BlockSyntax block)
    {
        var caught = (block.Parent as CatchClauseSyntax)?.Declaration?.Identifier.ValueText;
        if (string.IsNullOrEmpty(caught)) yield break;

        foreach (var call in block.DescendantNodes(LocalSemantics.DescendInto).OfType<InvocationExpressionSyntax>())
        {
            var name = call.Expression switch
            {
                MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                IdentifierNameSyntax id => id.Identifier.ValueText,
                _ => null,
            };
            if (name is null || name is "ToString" or "GetType" or "Log" or "LogError" or "LogWarning" or "Add" or "WriteLine") continue;
            var target = call.Expression is MemberAccessExpressionSyntax access ? access.Expression : null;
            var involvesCaught = (target?.DescendantNodesAndSelf() ?? []).Concat(call.ArgumentList.DescendantNodes())
                .OfType<IdentifierNameSyntax>().Any(id => id.Identifier.ValueText == caught);
            if (involvesCaught) yield return (call, name);
        }
    }
}
