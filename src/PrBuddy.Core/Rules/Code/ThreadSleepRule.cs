using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB012: Thread.Sleep in new production code. It blocks a thread doing nothing, and a fixed wait
/// is usually standing in for waiting on the real event. Tests are skipped (they often need it).
/// </summary>
public sealed class ThreadSleepRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB012", "Thread.Sleep in production code", Severity.Info, "Async");

    protected override bool AppliesTo(AnalyzedFile file) => !file.IsTestFile;

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root) =>
        root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(i => i.Expression is MemberAccessExpressionSyntax
            {
                Name.Identifier.ValueText: "Sleep",
                Expression: IdentifierNameSyntax { Identifier.ValueText: "Thread" } or MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Thread" },
            } && file.IsNew(i))
            .Select(i => Report(file, i, InAsyncMethod(i)
                ? "Thread.Sleep inside an async method blocks the thread. Use 'await Task.Delay(...)', or better, wait for the thing itself."
                : "Thread.Sleep blocks a thread doing nothing. If it's waiting for something to happen, wait on that instead."));

    static bool InAsyncMethod(SyntaxNode node) =>
        node.Ancestors().FirstOrDefault(a => a is MethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax) switch
        {
            MethodDeclarationSyntax m => m.Modifiers.Any(t => t.ValueText == "async"),
            LocalFunctionStatementSyntax l => l.Modifiers.Any(t => t.ValueText == "async"),
            AnonymousFunctionExpressionSyntax f => f.AsyncKeyword.ValueText == "async",
            _ => false,
        };
}
