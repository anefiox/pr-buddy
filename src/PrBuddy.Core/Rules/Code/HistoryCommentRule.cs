// prbuddy-ignore-file PRB033: the doc comment quotes the phrases this rule looks for
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB033: new comments that tell the story of the change ("the former recursive algorithm",
/// "used to take", "replacing the old code", "this PR adds") instead of explaining the code as it
/// is. The history belongs in the commit message; once merged, the comment describes code nobody
/// can see. One finding per file. Test files are skipped: "this used to throw" is how a regression
/// test says what it guards against.
/// </summary>
public sealed partial class HistoryCommentRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB033", "Comment narrates the change", Severity.Info, "Comments");

    protected override bool AppliesTo(AnalyzedFile file) => !file.IsTestFile;

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        var hits = new List<(SyntaxTrivia Trivia, string Phrase)>();
        foreach (var trivia in root.DescendantTrivia(descendIntoTrivia: true))
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) && !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia) &&
                !trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) && !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia) ||
                !file.IsNew(trivia))
                continue;
            if (Narration().Match(trivia.ToString()) is { Success: true } match)
                hits.Add((trivia, match.Value));
        }
        if (hits.Count == 0) yield break;

        var phrases = hits.Select(h => $"\"{h.Phrase.Trim()}\"").Distinct(StringComparer.OrdinalIgnoreCase).Take(3);
        yield return Report(file.Path, AnalyzedFile.LineOf(hits[0].Trivia),
            $"{(hits.Count == 1 ? "A new comment describes" : $"{hits.Count} new comments describe")} how the code changed ({string.Join(", ", phrases)}) " +
            "rather than what it does now. After merge nobody can see the old version. Move the history to the commit message " +
            "and keep comments for invariants and reasons.");
    }

    [GeneratedRegex(
        @"\b(the former|used to (?:be|take|return|call|do|have|work|run|throw|recurse)|" +
        @"(?:was|were|has been|have been) (?:changed|replaced|rewritten|refactored|moved) (?:to|from|with|into|out of)|" +
        @"replac(?:es|ing|ed) the (?:old|former|previous|original)|" +
        @"the (?:old|original|previous|prior) (?:code|implementation|version|approach|logic|behaviou?r|method|algorithm)|" +
        @"now (?:uses|calls|delegates)|" +
        @"this (?:PR|pull request|commit|change|rewrite|refactor) (?:adds|changes|fixes|introduces|moves|removes|replaces|rewrites|makes)|" +
        @"in this (?:PR|pull request|commit))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Narration();
}
