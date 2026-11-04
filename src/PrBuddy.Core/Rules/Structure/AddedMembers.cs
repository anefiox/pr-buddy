using Microsoft.CodeAnalysis.CSharp.Syntax;
using PrBuddy.Core.Semantics;

namespace PrBuddy.Core.Rules.Structure;

internal static class AddedMembers
{
    /// <summary>Declarations the semantic diff says are new in this PR (not edited, not renamed).</summary>
    public static IEnumerable<MemberDeclarationSyntax> In(AnalyzedFile file)
    {
        if (file.NewTree is null || file.SemanticDiff is not { } diff) yield break;

        var addedLines = diff.Changes
            .Where(c => c.Change == MemberChangeKind.Added && c.Line is not null)
            .Select(c => c.Line!.Value)
            .ToHashSet();
        if (addedLines.Count == 0) yield break;

        foreach (var member in file.NewTree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>())
            if (member is not BaseNamespaceDeclarationSyntax && addedLines.Contains(AnalyzedFile.LineOf(member)))
                yield return member;
    }

    /// <summary>"Outer.Inner.Method", for messages.</summary>
    public static string QualifiedName(MemberDeclarationSyntax member, string name)
    {
        var types = member.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.Text);
        return string.Join(".", types.Append(name));
    }
}
