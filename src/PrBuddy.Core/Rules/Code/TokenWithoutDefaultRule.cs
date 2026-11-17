using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB041: a method the PR adds or changes takes a CancellationToken with no default, and the PR's
/// own callers pass CancellationToken.None (or default) to it three times or more. Making the
/// parameter "= default" lets those callers drop the argument, and matches the usual .NET pattern.
/// </summary>
public sealed class TokenWithoutDefaultRule : RuleBase
{
    public override RuleDescriptor Descriptor { get; } = new("PRB041", "CancellationToken without a default", Severity.Info, "Code");

    const int Threshold = 3;

    public override IEnumerable<Finding> Analyze(PullRequestContext context)
    {
        var files = context.CSharpFiles.Where(f => f.NewTree is not null).ToArray();

        // Calls that pass a "nothing" token as their last argument, counted by method name.
        var emptyTokenCalls = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in files)
        foreach (var call in file.NewTree!.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (!file.IsNew(call) || call.ArgumentList.Arguments.LastOrDefault()?.Expression is not { } last || !IsEmptyToken(last)) continue;
            var name = call.Expression switch
            {
                MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                IdentifierNameSyntax id => id.Identifier.ValueText,
                GenericNameSyntax g => g.Identifier.ValueText,
                _ => null,
            };
            if (name is not null) emptyTokenCalls[name] = emptyTokenCalls.GetValueOrDefault(name) + 1;
        }
        if (emptyTokenCalls.Count == 0) yield break;

        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files.Where(f => !f.IsTestFile))
        foreach (var method in file.NewTree!.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var token = method.ParameterList.Parameters.LastOrDefault();
            var name = method.Identifier.ValueText;
            if (token is null || token.Default is not null || token.Type?.ToString() is not ("CancellationToken" or "System.Threading.CancellationToken") ||
                !file.TouchesAddedLines(token) || method.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword)) ||
                emptyTokenCalls.GetValueOrDefault(name) < Threshold || !reported.Add(name))
                continue;

            yield return Report(file, token,
                $"'{name}' takes '{token.Identifier.ValueText}' with no default, and {emptyTokenCalls[name]} calls in this PR pass " +
                $"CancellationToken.None or default to it. Declare it '{token.Type} {token.Identifier.ValueText} = default' so those callers can leave it out.");
        }
    }

    static bool IsEmptyToken(ExpressionSyntax e) => e switch
    {
        MemberAccessExpressionSyntax { Name.Identifier.ValueText: "None", Expression: var t } => t.ToString() is "CancellationToken" or "System.Threading.CancellationToken",
        LiteralExpressionSyntax l => l.IsKind(SyntaxKind.DefaultLiteralExpression),
        DefaultExpressionSyntax d => d.Type.ToString() is "CancellationToken" or "System.Threading.CancellationToken",
        _ => false,
    };
}
