using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB031: a field, constant, property or variable named like a credential (ClientSecret, ApiKey,
/// AccessToken, ClientId) set to a random-looking string literal. PRB010 only knows formats with a
/// distinctive shape; this catches the ones that don't have one. A secret-like name is a warning;
/// an id (ClientId, AppId) is info, since public clients may embed one, but its origin is worth a note.
/// The value is never printed.
/// </summary>
public sealed partial class HardcodedCredentialValueRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB031", "Hard-coded credential value", Severity.Warning, "Security");

    protected override bool AppliesTo(AnalyzedFile file) => !file.IsTestFile;

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var literal in root.DescendantNodes().OfType<LiteralExpressionSyntax>())
        {
            if (!literal.IsKind(SyntaxKind.StringLiteralExpression) || !file.IsNew(literal) ||
                !LooksRandom(literal.Token.ValueText) || NameFor(literal) is not { } name)
                continue;

            var secret = SecretName().IsMatch(name);
            if (!secret && !IdName().IsMatch(name)) continue;

            yield return Report(file, literal, secret
                ? $"'{name}' is set to a hard-coded value that looks like a real credential ({literal.Token.ValueText.Length} characters, not shown). " +
                  "Anyone with the code has it. Load it from configuration or a secret store, and rotate it if it was ever real."
                : $"'{name}' is set to a hard-coded value that looks like a real registered id ({literal.Token.ValueText.Length} characters). " +
                  "Say where it came from and who owns the registration, or make it configurable.",
                secret ? Severity.Warning : Severity.Info);
        }
    }

    /// <summary>The name a literal is assigned to: const X = "..", X = "..", new { X = ".." }, X: "..".</summary>
    static string? NameFor(LiteralExpressionSyntax literal) => literal.Parent switch
    {
        EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax v } => v.Identifier.ValueText,
        EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax p } => p.Identifier.ValueText,
        ArrowExpressionClauseSyntax { Parent: PropertyDeclarationSyntax p } => p.Identifier.ValueText,
        AssignmentExpressionSyntax { Left: var left } => left switch
        {
            IdentifierNameSyntax id => id.Identifier.ValueText,
            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
            _ => null,
        },
        ArgumentSyntax { NameColon.Name.Identifier.ValueText: var argName } => argName,
        _ => null,
    };

    /// <summary>20+ characters of letters and digits that mix cases or digits: a key, not a word or a URL.</summary>
    static bool LooksRandom(string value) =>
        value.Length is >= 20 and <= 200 && Token().IsMatch(value) &&
        value.Any(char.IsDigit) && value.Any(char.IsUpper) && value.Any(char.IsLower) &&
        !value.Contains("example", StringComparison.OrdinalIgnoreCase) && value.Distinct().Count() >= 10;

    [GeneratedRegex(@"^[A-Za-z0-9_\-+/=.]+$")]
    private static partial Regex Token();

    [GeneratedRegex(@"(Secret|ApiKey|AppKey|AccessKey|PrivateKey|AccessToken|AuthToken|BearerToken|RefreshToken|Password|Passphrase)$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretName();

    [GeneratedRegex(@"(ClientId|AppId|ApplicationId|ConsumerKey|TenantId)$", RegexOptions.IgnoreCase)]
    private static partial Regex IdName();
}
