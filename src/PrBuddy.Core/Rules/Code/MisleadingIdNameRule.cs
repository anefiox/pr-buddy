using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB025: a new parameter named like an id (orderId) whose type is a whole object (Order). Readers
/// and callers take it for a number or key.
/// </summary>
public sealed class MisleadingIdNameRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB025", "Object named like an id", Severity.Info, "Code");

    static readonly string[] IdTypeEndings = ["Id", "ID", "Key", "Identifier", "Guid", "Ulid", "Uuid", "Handle", "Ref", "Reference"];
    static readonly HashSet<string> IdTypes = new(StringComparer.Ordinal)
        { "Guid", "Ulid", "ObjectId", "String", "Int32", "Int64", "UInt32", "UInt64", "Uri", "BigInteger", "Hash256", "Address" };

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var parameter in root.DescendantNodes().OfType<ParameterSyntax>())
        {
            var name = parameter.Identifier.ValueText;
            if (!file.IsNew(parameter) || !name.EndsWith("Id", StringComparison.Ordinal) || name.Length <= 2) continue;

            var typeName = (parameter.Type is NullableTypeSyntax n ? n.ElementType : parameter.Type) switch
            {
                IdentifierNameSyntax id => id.Identifier.ValueText,
                QualifiedNameSyntax { Right: IdentifierNameSyntax right } => right.Identifier.ValueText,
                _ => null, // predefined types, generics, arrays: fine
            };
            if (typeName is null || IdTypes.Contains(typeName) || IdTypeEndings.Any(e => typeName.EndsWith(e, StringComparison.Ordinal)))
                continue;

            var stem = name[..^2];
            if (!typeName.EndsWith(stem, StringComparison.OrdinalIgnoreCase)) continue; // Order orderId, CustomerOrder orderId

            yield return Report(file, parameter,
                $"Parameter '{name}' holds a whole '{typeName}', not an id. Name it '{stem}' so callers don't pass, or expect, a key.");
        }
    }
}
