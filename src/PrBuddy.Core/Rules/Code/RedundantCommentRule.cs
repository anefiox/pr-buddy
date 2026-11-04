using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB008: a comment that just repeats the line below it, like "// increment the counter" above
/// "counter++;". Works by checking whether the comment's words already appear in the code.
/// </summary>
public sealed partial class RedundantCommentRule : AddedCodeRule
{
    /// <summary>Comments longer than this probably explain something, so they're left alone.</summary>
    const int MaxWords = 6;

    static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "to", "of", "and", "or", "for", "this", "that", "these", "those", "is", "are", "be",
        "it", "its", "in", "on", "at", "by", "with", "from", "as", "we", "our", "then", "now", "here", "if",
        "all", "through", "over", "into", "call", "calls", "method", "function", "variable", "property", "field",
    };

    /// <summary>Words a keyword "says" in English, so "// create a user" matches "new User()".</summary>
    static readonly Dictionary<string, string[]> KeywordSynonyms = new(StringComparer.Ordinal)
    {
        ["new"] = ["create", "instantiate", "initialize", "init", "construct"],
        ["if"] = ["check", "whether"],
        ["foreach"] = ["loop", "iterate", "each"],
        ["for"] = ["loop", "iterate"],
        ["while"] = ["loop"],
        ["await"] = ["wait"],
        ["var"] = ["declare"],
    };

    static readonly (string Operator, string[] Words)[] OperatorSynonyms =
    [
        ("++", ["increment", "increase"]),
        ("--", ["decrement", "decrease"]),
        ("+=", ["add", "increment", "increase"]),
        ("-=", ["subtract", "decrement", "decrease"]),
        ("==", ["compare", "equal", "check"]),
        ("!=", ["compare", "check"]),
        ("=", ["set", "assign", "store"]),
    ];

    public override RuleDescriptor Descriptor { get; } = new("PRB008", "Comment restates the code", Severity.Info, "Noise");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        var text = root.SyntaxTree.GetText();
        foreach (var token in root.DescendantTokens())
        {
            // Exactly one comment directly above the token; a multi-line comment block is an explanation.
            var comments = token.LeadingTrivia.Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia)).ToList();
            if (comments.Count != 1 || !file.IsNew(comments[0])) continue;

            var commentLine = AnalyzedFile.LineOf(comments[0]);
            var codeLine = AnalyzedFile.LineOf(token);
            if (codeLine != commentLine + 1) continue;

            var comment = comments[0].ToString().TrimStart('/').Trim();
            if (IsRedundant(comment, text.Lines[codeLine - 1].ToString()))
                yield return Report(file.Path, commentLine, $"Comment just restates the code: \"{SyntaxHelpers.Truncate(comment)}\"");
        }
    }

    public static bool IsRedundant(string comment, string codeLine)
    {
        if (SectionMarker().IsMatch(comment)) return false;

        var words = Words(comment).Where(w => !StopWords.Contains(w)).Select(Stem).Distinct().ToList();
        if (words.Count == 0 || words.Count > MaxWords) return false;

        // Every word must already be in the code: a single extra word ("toggles", "because") is
        // usually the one piece of information the comment adds.
        var code = CodeWords(codeLine);
        return words.All(code.Contains);
    }

    static HashSet<string> CodeWords(string line)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Identifier().Matches(line))
        {
            var raw = match.Value.ToLowerInvariant();
            if (KeywordSynonyms.TryGetValue(raw, out var synonyms))
                foreach (var synonym in synonyms) words.Add(Stem(synonym));
            foreach (var part in SplitIdentifier(match.Value)) words.Add(Stem(part));
        }
        foreach (var (op, synonyms) in OperatorSynonyms)
            if (line.Contains(op, StringComparison.Ordinal))
                foreach (var synonym in synonyms) words.Add(Stem(synonym));
        return words;
    }

    static IEnumerable<string> Words(string text) =>
        Identifier().Matches(text).SelectMany(m => SplitIdentifier(m.Value)).Where(w => w.Length > 1);

    /// <summary>"SaveChangesAsync" → save, changes, async.</summary>
    static IEnumerable<string> SplitIdentifier(string identifier) =>
        CamelBoundary().Split(identifier).Where(p => p.Length > 0).Select(p => p.ToLowerInvariant());

    /// <summary>Crude suffix stripping, applied to both sides so "updates" matches "Update".</summary>
    static string Stem(string word)
    {
        foreach (var suffix in (ReadOnlySpan<string>)["ing", "ed", "es", "s"])
        {
            if (word.Length - suffix.Length < 3 || !word.EndsWith(suffix, StringComparison.Ordinal)) continue;
            if (suffix == "s" && word.EndsWith("ss", StringComparison.Ordinal)) continue;
            word = word[..^suffix.Length];
            break;
        }
        return word.Length > 3 && word.EndsWith('e') ? word[..^1] : word;
    }

    [GeneratedRegex(@"^(arrange|act|assert|given|when|then)([\s&/,+-]+(arrange|act|assert|given|when|then))*:?$", RegexOptions.IgnoreCase)]
    private static partial Regex SectionMarker();

    [GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*")]
    private static partial Regex Identifier();

    [GeneratedRegex(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])|_")]
    private static partial Regex CamelBoundary();
}
