using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>Finds new synchronous call cycles between a loop and a helper in the same type and file.</summary>
public sealed class RecursiveLoopReentryRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB014", "Recursive loop re-entry", Severity.Warning, "Code");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        var candidates = FindCycles(root).Select(c => (Cycle: c, Line: ChangedLine(file, c)))
            .Where(c => c.Line is not null).ToArray();
        if (candidates.Length == 0) yield break;

        var oldCycles = file.OldTree is { } oldTree
            ? FindCycles(oldTree.GetRoot()).Select(c => c.Key).ToHashSet()
            : [];
        foreach (var (cycle, line) in candidates)
        {
            if (!oldCycles.Add(cycle.Key)) continue;
            yield return Report(file.Path, line,
                $"The loop in '{cycle.Dispatcher.Name}' calls '{cycle.Helper.Name}', which calls '{cycle.Dispatcher.Name}' again. " +
                "If this path repeats on deep input, it can grow the CLR stack despite the loop. " +
                "Schedule the next step through the loop instead of calling back into it.");
        }
    }

    static int? ChangedLine(AnalyzedFile file, Cycle cycle)
    {
        // Prefer the call that re-enters the loop. If that call already existed, point at the
        // newly added dispatch that closes the cycle.
        foreach (var call in new[] { cycle.Reentry, cycle.Dispatch })
        {
            if (LocalSemantics.AddedLine(file, call) is { } line) return line;
        }
        return null;
    }

    static IEnumerable<Cycle> FindCycles(SyntaxNode root)
    {
        var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => !m.Modifiers.Any(SyntaxKind.AsyncKeyword))
            .Select(m => (Method: m, Nodes: m.DescendantNodes(LocalSemantics.DescendInto).ToArray()))
            .Where(m => !m.Nodes.OfType<YieldStatementSyntax>().Any())
            .ToArray();
        var names = methods.Select(m => m.Method.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        var invocations = methods.Select(m => (m.Method, Calls: m.Nodes.OfType<InvocationExpressionSyntax>()
            .Where(call => CallName(call) is { } name && names.Contains(name))
            .Select(call => (Node: call, InLoop: call.Ancestors().TakeWhile(n => n != m.Method).Any(IsLoop)))
            .ToArray())).Where(m => m.Calls.Length > 0).ToArray();
        if (!invocations.Any(m => m.Calls.Any(call => call.InLoop))) yield break;

        // Bind against the file itself without loading or building the project.
        var model = LocalSemantics.Create(root.SyntaxTree);
        var edges = new Dictionary<IMethodSymbol, List<Call>>(SymbolEqualityComparer.Default);
        foreach (var (method, calls) in invocations)
        {
            if (model.GetDeclaredSymbol(method) is not { } caller) continue;
            var resolved = new List<Call>();
            foreach (var (call, inLoop) in calls)
            {
                if (LocalSemantics.ResolveCall(model, call) is not { } target ||
                    target.MethodKind != MethodKind.Ordinary ||
                    !SymbolEqualityComparer.Default.Equals(caller.ContainingType, target.ContainingType) ||
                    SymbolEqualityComparer.Default.Equals(caller, target.OriginalDefinition) ||
                    call.Expression is MemberAccessExpressionSyntax { Expression: not ThisExpressionSyntax } && !target.IsStatic)
                    continue;
                resolved.Add(new Call(target.OriginalDefinition, call, inLoop));
            }
            if (resolved.Count > 0) edges[caller] = resolved;
        }

        foreach (var (dispatcher, calls) in edges)
        foreach (var dispatch in calls.Where(c => c.InLoop))
        {
            if (!edges.TryGetValue(dispatch.Target, out var helperCalls)) continue;
            foreach (var reentry in helperCalls.Where(c => SymbolEqualityComparer.Default.Equals(c.Target, dispatcher)))
                yield return new Cycle(dispatcher, dispatch.Target, dispatch.Node, reentry.Node);
        }
    }

    static bool IsLoop(SyntaxNode node) =>
        node is WhileStatementSyntax or DoStatementSyntax or ForStatementSyntax or CommonForEachStatementSyntax;

    static string? CallName(InvocationExpressionSyntax call) => call.Expression switch
    {
        SimpleNameSyntax name => name.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null
    };

    sealed record Call(IMethodSymbol Target, InvocationExpressionSyntax Node, bool InLoop);

    sealed record Cycle(IMethodSymbol Dispatcher, IMethodSymbol Helper,
        InvocationExpressionSyntax Dispatch, InvocationExpressionSyntax Reentry)
    {
        public (string? First, string? Second) Key
        {
            get
            {
                var dispatcher = Dispatcher.GetDocumentationCommentId();
                var helper = Helper.GetDocumentationCommentId();
                return string.CompareOrdinal(dispatcher, helper) <= 0 ? (dispatcher, helper) : (helper, dispatcher);
            }
        }
    }
}
