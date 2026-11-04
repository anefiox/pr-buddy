using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules;

internal static partial class SyntaxHelpers
{
    /// <summary>"System.Diagnostics.CodeAnalysis.SuppressMessageAttribute" → "SuppressMessage".</summary>
    public static string AttributeName(AttributeSyntax attribute)
    {
        var name = attribute.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            AliasQualifiedNameSyntax a => a.Name.Identifier.Text,
            SimpleNameSyntax s => s.Identifier.Text,
            _ => attribute.Name.ToString(),
        };
        return name.Length > "Attribute".Length && name.EndsWith("Attribute", StringComparison.Ordinal)
            ? name[..^"Attribute".Length]
            : name;
    }

    public static string Truncate(string text, int max = 70)
    {
        text = Whitespace().Replace(text, " ").Trim();
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
