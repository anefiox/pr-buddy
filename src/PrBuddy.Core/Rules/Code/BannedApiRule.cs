using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PrBuddy.Core.Configuration;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB034: a call to an API the repository has banned in prbuddy.json, with the reason it gives.
/// Matching is by name, since PR Buddy doesn't compile the code:
///   "Utf8Parser.TryParse"     matches Utf8Parser.TryParse(...) and System.Buffers.Text.Utf8Parser.TryParse(...)
///   ".Chunk"                  matches any member called Chunk, such as LINQ's items.Chunk(64)
///   "AccreditationApiMapper"  (no dot) matches any member used through that type: AccreditationApiMapper.BuildHistory(...)
/// An entry can be scoped to paths, which makes it a layering rule:
///   "AccreditationApiMapper": { "reason": "The domain model must not depend on the API layer", "in": ["**/DomainModel/**"] }
/// Tests are skipped unless "includeTests" is true. With no "apis" configured the rule does nothing.
/// </summary>
public sealed class BannedApiRule(IReadOnlyDictionary<string, BannedApi> apis, bool includeTests = false) : AddedCodeRule
{
    public BannedApiRule(IReadOnlyDictionary<string, string> reasons, bool includeTests = false)
        : this(reasons.ToDictionary(r => r.Key, r => new BannedApi(r.Value, [])), includeTests) { }

    public override RuleDescriptor Descriptor { get; } = new("PRB034", "Banned API", Severity.Warning, "Code");

    protected override bool AppliesTo(AnalyzedFile file) => apis.Count > 0 && (includeTests || !file.IsTestFile);

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        var here = apis.Where(a => a.Value.AppliesTo(file.Path)).ToArray();
        if (here.Length == 0) yield break;

        var reported = new HashSet<(int Line, string Api)>();
        foreach (var access in root.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (!file.IsNew(access)) continue;
            var text = access.WithoutTrivia().ToString();
            var member = access.Name.Identifier.ValueText;
            var receiver = access.Expression.WithoutTrivia().ToString().Split('.')[^1];

            foreach (var (api, banned) in here)
            {
                var hit = api.StartsWith('.') ? member == api[1..]
                    : api.Contains('.') ? text == api || text.EndsWith("." + api, StringComparison.Ordinal)
                    : receiver == api;
                if (!hit) continue;

                var line = AnalyzedFile.LineOf(access.Name);
                if (reported.Add((line, api)))
                    yield return Report(file.Path, line,
                        $"'{(api.StartsWith('.') ? api : SyntaxHelpers.Truncate(text, 60))}' is banned " +
                        (banned.In.Count > 0 ? "in this part of the code" : "in this repository") +
                        (banned.Reason.Length > 0 ? $": {banned.Reason}" : "."));
                break;
            }
        }
    }
}
