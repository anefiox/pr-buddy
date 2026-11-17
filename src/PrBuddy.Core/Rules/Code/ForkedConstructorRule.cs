using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB039: a new constructor that builds its base the same way another constructor does
/// (both call base(new X(...))) instead of chaining to it with this(...). The construction logic
/// forks, so a later change to one path quietly misses the other.
/// </summary>
public sealed class ForkedConstructorRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB039", "Constructor forks instead of chaining", Severity.Info, "Code");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var builders = type.Members.OfType<ConstructorDeclarationSyntax>()
                .Where(c => !c.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
                .Select(c => (Ctor: c, Built: BuiltBase(c)))
                .Where(c => c.Built is not null)
                .ToArray();

            foreach (var (ctor, built) in builders)
            {
                if (!file.IsNew(ctor.Identifier)) continue;
                var other = builders.FirstOrDefault(b => b.Ctor != ctor && b.Built == built &&
                    // Prefer pointing at an existing constructor, the one the new one should chain to.
                    !file.IsNew(b.Ctor.Identifier));
                other = other.Ctor is null ? builders.FirstOrDefault(b => b.Ctor != ctor && b.Built == built && b.Ctor.SpanStart < ctor.SpanStart) : other;
                if (other.Ctor is null) continue;

                yield return Report(file.Path, AnalyzedFile.LineOf(ctor.Initializer!),
                    $"This constructor builds base(new {built}(...)) itself, like the constructor at line {AnalyzedFile.LineOf(other.Ctor.Identifier)}. " +
                    "Chain to that one with this(...), passing the new values, so construction happens in one place.");
            }
        }
    }

    /// <summary>The type a constructor creates for its base: base(new X(...)) gives X.</summary>
    static string? BuiltBase(ConstructorDeclarationSyntax ctor) =>
        ctor.Initializer is { ThisOrBaseKeyword.RawKind: (int)SyntaxKind.BaseKeyword } initializer &&
        initializer.ArgumentList.Arguments.Select(a => a.Expression).OfType<ObjectCreationExpressionSyntax>().FirstOrDefault() is { } creation
            ? creation.Type.ToString()
            : null;
}
