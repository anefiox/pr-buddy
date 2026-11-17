using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB022: a parameter of a new method that the body never reads. Only where no outside contract
/// can need it: non-public, non-virtual methods, and request parameters that ASP.NET binds with
/// [FromQuery], [FromBody] and the like.
/// </summary>
public sealed class UnusedParameterRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB022", "Unused parameter", Severity.Warning, "Code");

    static readonly HashSet<string> BindingAttributes = new(StringComparer.Ordinal)
        { "FromQuery", "FromBody", "FromRoute", "FromForm", "FromHeader" };

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            if (method.Body is null && method.ExpressionBody is null || !file.IsNew(method.Identifier) ||
                method.Modifiers.Any(m => m.Kind() is SyntaxKind.OverrideKeyword or SyntaxKind.VirtualKeyword or
                    SyntaxKind.AbstractKeyword or SyntaxKind.ExternKeyword or SyntaxKind.PartialKeyword) ||
                method.ExplicitInterfaceSpecifier is not null || ThrowsOnly(method))
                continue;

            var publicApi = method.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword)) ||
                method.Parent is InterfaceDeclarationSyntax;
            var used = (method.Body ?? (SyntaxNode)method.ExpressionBody!).DescendantNodes()
                .OfType<IdentifierNameSyntax>().Select(id => id.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);

            foreach (var parameter in method.ParameterList.Parameters)
            {
                var name = parameter.Identifier.ValueText;
                if (used.Contains(name) || name.StartsWith('_') || IsEventArgs(parameter) || parameter.Modifiers.Any(m => m.IsKind(SyntaxKind.ThisKeyword)))
                    continue;

                var binding = parameter.AttributeLists.SelectMany(l => l.Attributes)
                    .Select(SyntaxHelpers.AttributeName).FirstOrDefault(BindingAttributes.Contains);
                if (publicApi && binding is null) continue; // an interface or caller elsewhere may need it

                yield return Report(file, parameter, binding is not null
                    ? $"'{method.Identifier.ValueText}' binds '{name}' with [{binding}] but never reads it, so whatever a caller sends there is ignored. Use it or remove it."
                    : $"Parameter '{name}' of new method '{method.Identifier.ValueText}' is never used. Remove it, or use it if it was meant to change the result.");
            }
        }
    }

    static bool IsEventArgs(ParameterSyntax parameter) =>
        parameter.Type?.ToString() is { } type && (type.EndsWith("EventArgs", StringComparison.Ordinal) ||
            parameter.Identifier.ValueText == "sender" && type is "object" or "object?");

    static bool ThrowsOnly(MethodDeclarationSyntax method) =>
        method.ExpressionBody?.Expression is ThrowExpressionSyntax ||
        method.Body?.Statements is [ThrowStatementSyntax] or [];
}
