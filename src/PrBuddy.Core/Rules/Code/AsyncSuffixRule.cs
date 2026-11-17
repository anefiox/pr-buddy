using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB021: a new non-public async method without the Async suffix, in a type whose other async
/// methods use it. Callers can't tell from the name that it has to be awaited.
/// </summary>
public sealed class AsyncSuffixRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB021", "Async method without the Async suffix", Severity.Info, "Code");

    protected override bool AppliesTo(AnalyzedFile file) => !file.IsTestFile;

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var asyncMethods = type.Members.OfType<MethodDeclarationSyntax>().Where(ReturnsTask).ToArray();
            var suffixed = asyncMethods.Count(m => m.Identifier.ValueText.EndsWith("Async", StringComparison.Ordinal));
            if (suffixed < 2) continue; // only enforce a convention the type already follows

            foreach (var method in asyncMethods)
            {
                var name = method.Identifier.ValueText;
                if (name.EndsWith("Async", StringComparison.Ordinal) || !file.IsNew(method.Identifier) ||
                    method.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword) ||
                        m.IsKind(SyntaxKind.OverrideKeyword)) ||
                    method.ExplicitInterfaceSpecifier is not null || method.AttributeLists.Count > 0)
                    continue;

                yield return Report(file.Path, method.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    $"New method '{name}' returns a task, but unlike {suffixed} other methods in " +
                    $"'{type.Identifier.ValueText}' it has no Async suffix. Rename it to '{name}Async'.");
            }
        }
    }

    static bool ReturnsTask(MethodDeclarationSyntax method)
    {
        var type = method.ReturnType switch
        {
            GenericNameSyntax g => g.Identifier.ValueText,
            QualifiedNameSyntax { Right: var r } => r.Identifier.ValueText,
            IdentifierNameSyntax i => i.Identifier.ValueText,
            _ => "",
        };
        return type is "Task" or "ValueTask";
    }
}
