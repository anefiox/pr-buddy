using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB004: blocking on async code. This is syntax-only, so it can't know the receiver is really a Task;
/// .Result is reported as info for that reason.
/// </summary>
public sealed class SyncOverAsyncRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB004", "Blocking on async code", Severity.Warning, "Async");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (!file.IsNew(access.Name)) continue;
            var invocation = access.Parent as InvocationExpressionSyntax;
            var isCall = invocation is not null && invocation.Expression == access;

            switch (access.Name.Identifier.Text)
            {
                case "Wait" when isCall && invocation!.ArgumentList.Arguments.Count == 0:
                    yield return Report(file, access.Name, ".Wait() blocks the thread. If this is a Task it can deadlock. Use await.");
                    break;

                case "GetResult" when isCall && access.Expression is InvocationExpressionSyntax
                {
                    Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "GetAwaiter" }
                }:
                    yield return Report(file, access.Name, ".GetAwaiter().GetResult() blocks on async code. Use await.");
                    break;

                case "Result" when !isCall && !IsAssignmentTarget(access) && access.Expression is not ThisExpressionSyntax:
                    yield return Report(file, access.Name, "If this .Result is on a Task, it blocks the thread and can deadlock. Use await.", Severity.Info);
                    break;
            }
        }
    }

    static bool IsAssignmentTarget(ExpressionSyntax expression) =>
        expression.Parent is AssignmentExpressionSyntax assignment && assignment.Left == expression;
}
