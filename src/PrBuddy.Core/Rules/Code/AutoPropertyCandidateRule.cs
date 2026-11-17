using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB029: a new property that only reads (and maybe writes) a private field, where nothing else
/// needs the field itself. An auto-property says the same with one declaration instead of two.
/// </summary>
public sealed class AutoPropertyCandidateRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB029", "Could be an auto-property", Severity.Info, "Code");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var fields = type.Members.OfType<FieldDeclarationSyntax>()
                .Where(f => f.Declaration.Variables.Count == 1 && f.AttributeLists.Count == 0 &&
                    !f.Modifiers.Any(m => m.Kind() is SyntaxKind.PublicKeyword or SyntaxKind.ProtectedKeyword or SyntaxKind.InternalKeyword or
                        SyntaxKind.ConstKeyword or SyntaxKind.VolatileKeyword or SyntaxKind.FixedKeyword))
                .ToDictionary(f => f.Declaration.Variables[0].Identifier.ValueText, f => f, StringComparer.Ordinal);
            if (fields.Count == 0) continue;

            foreach (var property in type.Members.OfType<PropertyDeclarationSyntax>())
            {
                if (!file.IsNew(property.Identifier) || property.Modifiers.Any(m => m.Kind() is SyntaxKind.AbstractKeyword or SyntaxKind.ExternKeyword) ||
                    WrappedField(property) is not { } name || !fields.TryGetValue(name, out var field) ||
                    field.Declaration.Type.ToString() != property.Type.ToString() ||
                    field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)) != property.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
                    continue;

                // Elsewhere the field may only be assigned (a private setter covers that), never passed by ref,
                // locked on, or touched with Interlocked/Volatile.
                var uses = type.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Where(id => id.Identifier.ValueText == name && !property.Span.Contains(id.Span)).ToArray();
                if (uses.Any(id => id.Parent is ArgumentSyntax { RefKindKeyword.RawKind: not 0 } ||
                        id.Parent is LockStatementSyntax || id.Ancestors().OfType<RefExpressionSyntax>().Any()))
                    continue;

                var readOnlyField = field.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword));
                var writtenElsewhere = uses.Any(id => id.Parent is AssignmentExpressionSyntax a && a.Left == id ||
                    id.Parent is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } m && m.Parent is AssignmentExpressionSyntax a2 && a2.Left == m);
                var hasSetter = property.AccessorList?.Accessors.Any(a => a.Keyword.ValueText is "set" or "init") == true;
                var shape = hasSetter ? "{ get; set; }" : writtenElsewhere && !readOnlyField ? "{ get; private set; }" : "{ get; }";

                yield return Report(file, property,
                    $"Property '{property.Identifier.ValueText}' only wraps the field '{name}'. Make it an auto-property " +
                    $"({property.Type} {property.Identifier.ValueText} {shape}) and drop the field.");
            }
        }
    }

    /// <summary>The field a property just returns and assigns: => _x, get => _x, get { return _x; }, set => _x = value.</summary>
    static string? WrappedField(PropertyDeclarationSyntax property)
    {
        if (property.ExpressionBody is { Expression: var body }) return FieldName(body);
        if (property.AccessorList is not { } list) return null;

        string? field = null;
        foreach (var accessor in list.Accessors)
        {
            if (accessor.AttributeLists.Count > 0) return null;
            var expression = accessor.ExpressionBody?.Expression ?? accessor.Body?.Statements switch
            {
                [ReturnStatementSyntax r] => r.Expression,
                [ExpressionStatementSyntax e] => e.Expression,
                _ => null,
            };
            var name = accessor.Keyword.ValueText switch
            {
                "get" => expression is null ? null : FieldName(expression),
                "set" or "init" => expression is AssignmentExpressionSyntax { Right: IdentifierNameSyntax { Identifier.ValueText: "value" } } a &&
                    a.IsKind(SyntaxKind.SimpleAssignmentExpression) ? FieldName(a.Left) : null,
                _ => null,
            };
            if (name is null || field is not null && field != name) return null;
            field = name;
        }
        return field;
    }

    static string? FieldName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: IdentifierNameSyntax n } => n.Identifier.ValueText,
        _ => null,
    };
}
