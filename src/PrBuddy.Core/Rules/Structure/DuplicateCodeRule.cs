using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Structure;

/// <summary>
/// PRB302: a new method that's a near-copy of one already in the repo (or of another new one).
///
/// Bodies are compared as token sequences with local names and literals blanked out, so renaming
/// variables or changing a constant doesn't hide a copy. Member, type and method names are kept,
/// so two methods that merely share a shape ("call x, check y, return z") don't match.
/// Similarity is the Jaccard index of 4-token shingles.
/// </summary>
/// <param name="similarity">Report at or above this ("PRB302": { "similarity": 0.8 }).</param>
/// <param name="copySimilarity">Warning at or above this, info below ("copySimilarity": 0.95).</param>
/// <param name="minTokens">Ignore shorter bodies, which are too generic to call copies ("minTokens": 40).</param>
public sealed class DuplicateCodeRule(
    double similarity = DuplicateCodeRule.DefaultSimilarity,
    double copySimilarity = DuplicateCodeRule.DefaultCopySimilarity,
    int minTokens = DuplicateCodeRule.DefaultMinTokens) : RuleBase
{
    public const int DefaultMinTokens = 40;
    public const double DefaultSimilarity = 0.8;
    public const double DefaultCopySimilarity = 0.95;
    const int ShingleSize = 4;

    public override RuleDescriptor Descriptor { get; } = new("PRB302", "Duplicated code", Severity.Warning, "Structure");

    public override IEnumerable<Finding> Analyze(PullRequestContext context)
    {
        var candidates = context.CSharpFiles
            .SelectMany(f => AddedMembers.In(f).OfType<BaseMethodDeclarationSyntax>().Select(m => (File: f, Method: m)))
            .Select(c => (c.File, Fingerprint: Fingerprint.Of(c.File.Path, c.Method, minTokens, int.MaxValue)))
            .Where(c => c.Fingerprint is not null)
            .ToList();
        if (candidates.Count == 0) yield break;

        // Jaccard can't exceed the smaller set over the larger, so a method far smaller or larger
        // than every candidate can't match any of them: skip it before doing the expensive part.
        var slack = similarity * 0.95;
        var smallest = (int)Math.Floor(candidates.Min(c => c.Fingerprint!.TokenCount) * slack);
        var largest = (int)Math.Ceiling(candidates.Max(c => c.Fingerprint!.TokenCount) / slack);

        // Parallel, but ordered, so ties between equally similar matches resolve the same every run.
        var index = context.RepoTrees()
            .AsParallel().AsOrdered()
            .SelectMany(t => t.Tree.GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>()
                .Select(m => Fingerprint.Of(t.Path, m, Math.Max(minTokens, smallest), largest)))
            .Where(f => f is not null)
            .Select(f => f!)
            .ToList();

        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (file, candidate) in candidates)
        {
            Fingerprint? best = null;
            var bestScore = 0.0;
            foreach (var other in index)
            {
                if (other.Path == candidate!.Path && other.Line == candidate.Line) continue; // itself
                var ratio = (double)other.TokenCount / candidate.TokenCount;
                if (ratio < slack || ratio > 1 / slack) continue; // sizes too far apart to match
                var score = Jaccard(candidate.Shingles, other.Shingles);
                if (score > bestScore) (best, bestScore) = (other, score);
            }
            if (best is null || bestScore < similarity) continue;

            // Two new copies of each other: say it once.
            var pair = string.CompareOrdinal(candidate!.Key, best.Key) < 0 ? $"{candidate.Key}|{best.Key}" : $"{best.Key}|{candidate.Key}";
            if (!reported.Add(pair)) continue;

            yield return Report(file.Path, candidate.Line,
                $"'{candidate.Name}' is {Reporting.Numbers.Percent(bestScore)} similar to '{best.Name}' ({best.Path}:L{best.Line}). Reuse or extract it instead of keeping two copies?",
                bestScore >= copySimilarity ? Severity.Warning : Severity.Info);
        }
    }

    static double Jaccard(HashSet<ulong> a, HashSet<ulong> b)
    {
        var (small, large) = a.Count <= b.Count ? (a, b) : (b, a);
        var shared = small.Count(large.Contains);
        return (double)shared / (a.Count + b.Count - shared);
    }

    sealed record Fingerprint(string Path, int Line, string Name, int TokenCount, HashSet<ulong> Shingles)
    {
        public string Key => $"{Path}:{Line}";

        /// <param name="minTokens">Skip bodies with fewer tokens than this.</param>
        /// <param name="maxTokens">Skip bodies with more tokens than this (they can't match anything we're looking for).</param>
        public static Fingerprint? Of(string path, BaseMethodDeclarationSyntax method, int minTokens, int maxTokens)
        {
            // Tests are similar to their neighbours by design (arrange, act, assert with other inputs).
            if (method is MethodDeclarationSyntax m && Testing.TestSyntax.IsTestMethod(m)) return null;
            SyntaxNode? body = (SyntaxNode?)method.Body ?? method.ExpressionBody;
            if (body is null) return null;

            // Counting is cheap; normalizing and shingling is not, so size-check first.
            var count = body.DescendantTokens().Count();
            if (count < minTokens || count > maxTokens) return null;

            var locals = LocalNames(method);
            var tokens = body.DescendantTokens().Select(t => Hash(Normalize(t, locals))).ToArray();

            var shingles = new HashSet<ulong>();
            for (var i = 0; i + ShingleSize <= tokens.Length; i++)
            {
                var h = 14695981039346656037UL;
                for (var k = 0; k < ShingleSize; k++) h = (h ^ tokens[i + k]) * 1099511628211UL;
                shingles.Add(h);
            }

            return new Fingerprint(path, AnalyzedFile.LineOf(method), NameOf(method), tokens.Length, shingles);
        }

        /// <summary>64-bit FNV-1a. Not string.GetHashCode, which is randomized per process and would make results vary between runs.</summary>
        static ulong Hash(string text)
        {
            var h = 14695981039346656037UL;
            foreach (var c in text) h = (h ^ c) * 1099511628211UL;
            return h;
        }

        static string Normalize(SyntaxToken token, HashSet<string> locals) => token.Kind() switch
        {
            SyntaxKind.IdentifierToken when locals.Contains(token.ValueText) => "$v",
            SyntaxKind.NumericLiteralToken or SyntaxKind.StringLiteralToken or SyntaxKind.CharacterLiteralToken
                or SyntaxKind.InterpolatedStringTextToken or SyntaxKind.SingleLineRawStringLiteralToken
                or SyntaxKind.MultiLineRawStringLiteralToken => "$lit",
            _ => token.Text,
        };

        /// <summary>Parameters, locals, loop and catch variables, lambda parameters, pattern variables.</summary>
        static HashSet<string> LocalNames(BaseMethodDeclarationSyntax method)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in method.ParameterList.Parameters) names.Add(p.Identifier.ValueText);
            foreach (var node in method.DescendantNodes())
            {
                var identifier = node switch
                {
                    VariableDeclaratorSyntax v => v.Identifier,
                    ForEachStatementSyntax f => f.Identifier,
                    CatchDeclarationSyntax c => c.Identifier,
                    ParameterSyntax p => p.Identifier,
                    SingleVariableDesignationSyntax d => d.Identifier,
                    _ => default,
                };
                if (identifier.ValueText.Length > 0) names.Add(identifier.ValueText);
            }
            return names;
        }

        static string NameOf(BaseMethodDeclarationSyntax method)
        {
            var name = method switch
            {
                MethodDeclarationSyntax m => m.Identifier.Text,
                ConstructorDeclarationSyntax c => c.Identifier.Text,
                _ => method.Kind().ToString(),
            };
            var type = method.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;
            return type is null ? name : $"{type}.{name}";
        }
    }
}
