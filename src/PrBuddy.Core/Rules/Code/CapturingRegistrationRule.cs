using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB038: CancellationToken.Register or UnsafeRegister with a lambda that captures one variable.
/// The capture allocates a closure and a delegate on every call; the overload that takes a state
/// object, with a static lambda, does the same work without either. Info: it matters on hot paths.
/// </summary>
public sealed class CapturingRegistrationRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB038", "Cancellation callback captures a variable", Severity.Info, "Performance");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        var calls = root.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Register" or "UnsafeRegister" } &&
                c.ArgumentList.Arguments is [{ Expression: ParenthesizedLambdaExpressionSyntax { ParameterList.Parameters.Count: 0 } lambda }] &&
                !lambda.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) && file.IsNew(c))
            .ToArray();
        if (calls.Length == 0) yield break;

        var model = LocalSemantics.Create(root.SyntaxTree);
        var tokenType = model.Compilation.GetTypeByMetadataName("System.Threading.CancellationToken");
        foreach (var call in calls)
        {
            if (model.GetOperation(call) is not IInvocationOperation invocation ||
                !LocalSemantics.Same(invocation.TargetMethod.ContainingType, tokenType))
                continue;
            var lambda = (ParenthesizedLambdaExpressionSyntax)call.ArgumentList.Arguments[0].Expression;
            var captured = model.AnalyzeDataFlow(lambda)?.CapturedInside.Where(s => s is ILocalSymbol or IParameterSymbol).ToArray();
            if (captured is not [var only]) continue; // nothing captured, or more than a state object can carry neatly

            var type = only switch { ILocalSymbol l => l.Type, IParameterSymbol p => p.Type, _ => null };
            var typeName = type is null or IErrorTypeSymbol ? "T" : type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
            yield return Report(file, call,
                $"This callback captures '{only.Name}', so each call allocates a closure and a delegate. " +
                $"Pass it as state with a static lambda instead: Register(static state => ... (({typeName})state!) ..., {only.Name}).");
        }
    }
}
