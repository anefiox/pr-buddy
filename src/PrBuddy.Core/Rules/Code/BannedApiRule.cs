using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB034: a call to an API the repository has banned in prbuddy.json, with the reason it gives.
/// Matching is by name, since PR Buddy doesn't compile the code:
///   "Utf8Parser.TryParse"  matches Utf8Parser.TryParse(...) and System.Buffers.Text.Utf8Parser.TryParse(...)
///   ".Chunk"               matches any member called Chunk, such as LINQ's items.Chunk(64)
///   "Thread.Sleep"         matches Thread.Sleep(...) and System.Threading.Thread.Sleep(...)
/// Tests are skipped unless "includeTests" is true. With no "apis" configured the rule does nothing.
/// </summary>
public sealed class BannedApiRule(IReadOnlyDictionary<string, string> apis, bool includeTests = false) : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } = new("PRB034", "Banned API", Severity.Warning, "Code");

    protected override bool AppliesTo(AnalyzedFile file) => apis.Count > 0 && (includeTests || !file.IsTestFile);

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        var reported = new HashSet<(int Line, string Api)>();
        foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (!file.IsNew(access)) continue;
            var text = access.WithoutTrivia().ToString();
            var name = access.Name.Identifier.ValueText;
            foreach (var (api, reason) in apis)
            {
                var anyMember = api.StartsWith('.');
                var hit = anyMember
                    ? name == api[1..]
                    : text == api || text.EndsWith("." + api, StringComparison.Ordinal);
                if (!hit) continue;
                if (reported.Add((AnalyzedFile.LineOf(access.Name), api)))
                    yield return Report(file.Path, AnalyzedFile.LineOf(access.Name),
                        $"'{(anyMember ? api : SyntaxHelpers.Truncate(text, 60))}' is banned in this repository" + (reason.Length > 0 ? $": {reason}" : "."));
                break;
            }
        }
    }
}
