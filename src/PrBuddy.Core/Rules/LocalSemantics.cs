using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules;

internal static class LocalSemantics
{
    static readonly Lazy<MetadataReference[]> References = new(() =>
        new[] { typeof(object), typeof(Stack<>), typeof(TaskCompletionSource<>) }
            .Select(t => t.Assembly.Location).Distinct(StringComparer.Ordinal)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path)).ToArray());

    public static SemanticModel Create(SyntaxTree tree, IEnumerable<SyntaxTree>? relatedTrees = null) =>
        CSharpCompilation.Create("PrBuddy.Review", [tree, .. relatedTrees ?? []], References.Value)
            .GetSemanticModel(tree);

    public static SyntaxNode? Declaration(ISymbol symbol) =>
        symbol.DeclaringSyntaxReferences is [var reference] ? reference.GetSyntax() : null;

    public static bool Same(ISymbol? first, ISymbol? second) =>
        first is not null && SymbolEqualityComparer.Default.Equals(first, second);

    public static bool DescendInto(SyntaxNode node) =>
        node is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax or QueryExpressionSyntax);

    public static bool IsSelfCall(InvocationExpressionSyntax call) =>
        call.Expression is SimpleNameSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax };

    public static IMethodSymbol? ResolveCall(SemanticModel model, InvocationExpressionSyntax call)
    {
        var info = model.GetSymbolInfo(call);
        if (info.Symbol is IMethodSymbol method) return method;
        if (info.CandidateReason != CandidateReason.OverloadResolutionFailure ||
            info.CandidateSymbols is not [IMethodSymbol candidate]) return null;

        // Outside dependencies can leave arguments untyped. Replace only those arguments,
        // and require the same single candidate to bind with the original names and arity.
        var untyped = call.ArgumentList.Arguments.Where(a =>
            model.GetTypeInfo(a.Expression).Type is IErrorTypeSymbol).ToArray();
        if (untyped.Length == 0) return null;
        var speculativeCall = call.ReplaceNodes(untyped, (_, a) =>
            a.WithExpression(SyntaxFactory.LiteralExpression(SyntaxKind.DefaultLiteralExpression)));
        var resolved = model.GetSpeculativeSymbolInfo(call.SpanStart, speculativeCall,
            SpeculativeBindingOption.BindAsExpression).Symbol as IMethodSymbol;
        return Same(resolved?.OriginalDefinition, candidate.OriginalDefinition) ? resolved : null;
    }

    public static int? AddedLine(AnalyzedFile file, SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        for (var line = span.StartLinePosition.Line + 1; line <= span.EndLinePosition.Line + 1; line++)
            if (file.IsAddedLine(line)) return line;
        return null;
    }
}
