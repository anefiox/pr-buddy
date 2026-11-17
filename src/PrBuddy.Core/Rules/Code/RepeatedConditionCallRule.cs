using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB037: the same call, with the same arguments, used as a condition twice in one method:
/// var x = A(e) || B(e); ... if (A(e)) ... The second evaluation repeats work, and if the call
/// reads changing state the two can disagree. Info, since a call that's cheap and pure is harmless.
/// </summary>
public sealed class RepeatedConditionCallRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB037", "Condition computed twice", Severity.Info, "Code");

    static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
        { "Equals", "ReferenceEquals", "IsNullOrEmpty", "IsNullOrWhiteSpace", "Any", "Contains", "ContainsKey", "HasFlag", "nameof", "TryGetValue" };

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
        {
            if (method.Body is null || !file.TouchesAddedLines(method)) continue;
            var seen = new Dictionary<string, InvocationExpressionSyntax>(StringComparer.Ordinal);
            foreach (var call in method.Body.DescendantNodes(LocalSemantics.DescendInto).OfType<InvocationExpressionSyntax>())
            {
                if (call.ArgumentList.Arguments.Count == 0 || !InConditionPosition(call) || Name(call) is not { } name || Ignored.Contains(name))
                    continue;
                var key = call.WithoutTrivia().ToString();
                if (!seen.TryGetValue(key, out var first))
                {
                    seen[key] = call;
                    continue;
                }
                if (!file.IsNew(call) && !file.IsNew(first)) continue;
                if (ArgumentsAssignedBetween(method.Body, first, call) || OnlyOneRuns(first, call))
                {
                    seen[key] = call;
                    continue;
                }

                yield return Report(file, call,
                    $"'{SyntaxHelpers.Truncate(key, 60)}' is evaluated again here after line {AnalyzedFile.LineOf(first)}. " +
                    "Store the result in a local and use it in both places, so the two checks can't disagree.");
                seen[key] = call; // report each further repeat against the latest one
            }
        }
    }

    static string? Name(InvocationExpressionSyntax call) => call.Expression switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        _ => null,
    };

    /// <summary>Used as a yes/no decision: an if/while/ternary condition, an operand of &&, || or !.</summary>
    static bool InConditionPosition(InvocationExpressionSyntax call)
    {
        SyntaxNode node = call;
        while (node.Parent is ParenthesizedExpressionSyntax or PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.LogicalNotExpression } ||
               node.Parent is BinaryExpressionSyntax b && b.Kind() is SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression)
        {
            if (node.Parent is BinaryExpressionSyntax) return true;
            node = node.Parent;
        }
        return node.Parent switch
        {
            IfStatementSyntax i => i.Condition == node,
            WhileStatementSyntax w => w.Condition == node,
            ConditionalExpressionSyntax c => c.Condition == node,
            _ => false,
        };
    }

    /// <summary>The two calls sit in different sections of one switch, or the then and else branches of one if.</summary>
    static bool OnlyOneRuns(SyntaxNode first, SyntaxNode second)
    {
        var firstAncestors = first.Ancestors().ToList();
        var common = second.Ancestors().FirstOrDefault(firstAncestors.Contains);
        return common switch
        {
            SwitchStatementSyntax => first.FirstAncestorOrSelf<SwitchSectionSyntax>() != second.FirstAncestorOrSelf<SwitchSectionSyntax>(),
            SwitchExpressionSyntax => first.FirstAncestorOrSelf<SwitchExpressionArmSyntax>() != second.FirstAncestorOrSelf<SwitchExpressionArmSyntax>(),
            IfStatementSyntax ifStatement => ifStatement.Else is { } otherwise &&
                (ifStatement.Statement.Span.Contains(first.Span) && otherwise.Span.Contains(second.Span) ||
                 ifStatement.Statement.Span.Contains(second.Span) && otherwise.Span.Contains(first.Span)),
            ConditionalExpressionSyntax c => !c.Condition.Span.Contains(first.Span) && !c.Condition.Span.Contains(second.Span),
            _ => false,
        };
    }

    static bool ArgumentsAssignedBetween(BlockSyntax body, InvocationExpressionSyntax first, InvocationExpressionSyntax second)
    {
        var names = first.ArgumentList.Arguments.SelectMany(a => a.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            .Select(id => id.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        return body.DescendantNodes().OfType<AssignmentExpressionSyntax>().Any(a =>
            a.SpanStart > first.Span.End && a.SpanStart < second.SpanStart &&
            a.Left.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(id => names.Contains(id.Identifier.ValueText)));
    }
}
