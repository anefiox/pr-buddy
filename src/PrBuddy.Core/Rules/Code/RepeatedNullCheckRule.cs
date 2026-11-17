using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB040: a nullable parameter that a new method or constructor null-checks three times or more
/// (options?.A, options?.B, options ?? ...). Each check is another branch; defaulting it once
/// (options ??= new()) makes the rest of the body plain.
/// </summary>
public sealed class RepeatedNullCheckRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB040", "Parameter null-checked repeatedly", Severity.Info, "Code");

    const int Threshold = 3;

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var method in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
        {
            var body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
            if (body is null || !file.IsNew(method)) continue;

            // Options-style objects; int?/bool? have no "new()" default to suggest.
            foreach (var parameter in method.ParameterList.Parameters.Where(p => p.Type is NullableTypeSyntax { ElementType: not PredefinedTypeSyntax }))
            {
                var name = parameter.Identifier.ValueText;
                var checks = body.DescendantNodes(LocalSemantics.DescendInto).Count(n => n switch
                {
                    ConditionalAccessExpressionSyntax { Expression: IdentifierNameSyntax id } => id.Identifier.ValueText == name,
                    BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression, Left: IdentifierNameSyntax id } => id.Identifier.ValueText == name,
                    _ => false,
                });
                if (checks < Threshold) continue;

                var type = ((NullableTypeSyntax)parameter.Type!).ElementType;
                yield return Report(file, parameter,
                    $"'{name}' is null-checked {checks} times in this {(method is ConstructorDeclarationSyntax ? "constructor" : "method")}. " +
                    $"Default it once ({name} ??= new {type}();), or make the parameter non-nullable with a default, so the rest reads it plainly.");
            }
        }
    }
}
