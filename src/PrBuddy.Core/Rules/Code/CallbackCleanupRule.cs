using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace PrBuddy.Core.Rules.Code;

public sealed class CallbackCleanupRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } =
        new("PRB016", "Missing resource cleanup on alternate completion", Severity.Warning, "Code");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        var groups = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Select(call => call.ArgumentList.Arguments.Select(a => a.Expression).OfType<LambdaExpressionSyntax>().ToArray())
            .Where(callbacks => callbacks.Length > 1 && callbacks.Any(cb => file.TouchesAddedLines(cb)))
            .ToArray();
        if (groups.Length == 0) yield break;

        var model = LocalSemantics.Create(root.SyntaxTree);
        var completionType = model.Compilation.GetTypeByMetadataName("System.Threading.Tasks.TaskCompletionSource`1");
        var nongeneric = model.Compilation.GetTypeByMetadataName("System.Threading.Tasks.TaskCompletionSource");
        foreach (var group in groups)
        {
            var callbacks = group.Select(cb => Inspect(cb, model, completionType, nongeneric))
                .Where(cb => cb is not null).Cast<Callback>().ToArray();
            foreach (var callback in callbacks)
            {
                var line = LocalSemantics.AddedLine(file, callback.Syntax);
                if (line is null) continue;

                var missing = callbacks.Where(other => other != callback && LocalSemantics.Same(other.Completion, callback.Completion))
                    .SelectMany(other => other.Cleanup).Distinct(SymbolEqualityComparer.Default)
                    .Where(resource => !callback.References.Contains(resource)).Select(resource => resource!.Name).ToArray();
                if (missing.Length == 0) continue;
                yield return Report(file.Path, line,
                    $"This callback completes '{callback.Completion.Name}' without touching {string.Join(", ", missing)}, " +
                    "which another completion callback disposes or passes to a disposing helper. " +
                    "Check that this path also releases the resource.");
            }
        }
    }

    static Callback? Inspect(LambdaExpressionSyntax callback, SemanticModel model, INamedTypeSymbol? generic, INamedTypeSymbol? nongeneric)
    {
        var nodes = callback.Body.DescendantNodesAndSelf(LocalSemantics.DescendInto).ToArray();
        var calls = nodes.OfType<InvocationExpressionSyntax>().ToArray();
        var completions = calls.Select(call => Completion(call, model, generic, nongeneric)).OfType<ILocalSymbol>()
            .Distinct(SymbolEqualityComparer.Default).ToArray();
        if (completions is not [ILocalSymbol completion]) return null;

        var references = nodes.OfType<IdentifierNameSyntax>().Select(n => model.GetSymbolInfo(n).Symbol)
            .OfType<ISymbol>().ToHashSet(SymbolEqualityComparer.Default);
        var cleanup = calls.SelectMany(call => ResourcesReleasedBy(call, model))
            .Where(resource => resource is ILocalSymbol or IParameterSymbol)
            .Where(resource => LocalSemantics.Declaration(resource) is { } declaration && !callback.Span.Contains(declaration.Span))
            .ToHashSet(SymbolEqualityComparer.Default);
        return new Callback(callback, completion, references, cleanup);
    }

    static ILocalSymbol? Completion(InvocationExpressionSyntax call, SemanticModel model, INamedTypeSymbol? generic, INamedTypeSymbol? nongeneric)
    {
        if (call.Expression is not MemberAccessExpressionSyntax member ||
            member.Name.Identifier.ValueText is not ("SetResult" or "TrySetResult" or "SetCanceled" or "TrySetCanceled" or "SetException" or "TrySetException") ||
            model.GetSymbolInfo(member.Expression).Symbol is not ILocalSymbol { Type: INamedTypeSymbol type } local)
            return null;
        return LocalSemantics.Same(type.OriginalDefinition, generic) || LocalSemantics.Same(type, nongeneric) ? local : null;
    }

    static IEnumerable<ISymbol> ResourcesReleasedBy(InvocationExpressionSyntax call, SemanticModel model)
    {
        if (DisposalReceiver(call, model) is { } direct)
            yield return direct;

        if (!LocalSemantics.IsSelfCall(call) ||
            model.GetOperation(call) is not IInvocationOperation invocation ||
            LocalSemantics.Declaration(invocation.TargetMethod) is not MethodDeclarationSyntax method ||
            method.SyntaxTree != call.SyntaxTree)
            yield break;

        var disposedParameters = method.DescendantNodes(LocalSemantics.DescendInto).OfType<InvocationExpressionSyntax>()
            .Select(n => DisposalReceiver(n, model)).OfType<IParameterSymbol>().ToHashSet(SymbolEqualityComparer.Default);
        foreach (var argument in invocation.Arguments)
            if (disposedParameters.Contains(argument.Parameter) && model.GetSymbolInfo(argument.Value.Syntax).Symbol is { } resource)
                yield return resource;
    }

    static ISymbol? DisposalReceiver(InvocationExpressionSyntax call, SemanticModel model)
    {
        if (call.ArgumentList.Arguments.Count != 0) return null;
        var receiver = call.Expression switch
        {
            MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Dispose", Expression: var resource } => resource,
            MemberBindingExpressionSyntax { Name.Identifier.ValueText: "Dispose" }
                when call.Parent is ConditionalAccessExpressionSyntax conditional => conditional.Expression,
            MemberAccessExpressionSyntax { Name.Identifier.ValueText: "DisposeAsync", Expression: var resource }
                when call.Parent is AwaitExpressionSyntax => resource,
            _ => null
        };
        return receiver is null ? null : model.GetSymbolInfo(receiver).Symbol;
    }

    sealed record Callback(LambdaExpressionSyntax Syntax, ILocalSymbol Completion,
        HashSet<ISymbol> References, HashSet<ISymbol> Cleanup);
}
