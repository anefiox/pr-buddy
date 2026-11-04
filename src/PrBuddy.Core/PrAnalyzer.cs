using System.Text.Json.Serialization;
using PrBuddy.Core.Configuration;
using PrBuddy.Core.Rules;
using PrBuddy.Core.Semantics;

namespace PrBuddy.Core;

public sealed record FileReport(
    string Path,
    string? OldPath,
    ChangeStatus Status,
    FileKind Kind,
    int AddedLines,
    int DeletedLines,
    int RiskScore,
    FileSemanticDiff? SemanticDiff,
    IReadOnlyList<Finding> Findings)
{
    /// <summary>Generated, or nothing but formatting and comments, with no findings: no close read needed.</summary>
    public bool IsSkimmable =>
        Findings.Count == 0 && (Kind == FileKind.Generated || SemanticDiff is { IsCosmeticOnly: true });
}

/// <param name="Files">In suggested reading order: riskiest first, skimmable files last.</param>
/// <param name="IgnoredPaths">Changed files left out because prbuddy.json ignores them.</param>
/// <param name="Suppressed">Findings waved through by a prbuddy-ignore comment, with the reason given.</param>
public sealed record AnalysisReport(
    IReadOnlyList<FileReport> Files,
    IReadOnlyList<Finding> PullRequestFindings,
    IReadOnlyList<string> IgnoredPaths,
    IReadOnlyList<SuppressedFinding> Suppressed)
{
    [JsonIgnore] public IEnumerable<FileReport> ReviewFiles => Files.Where(f => !f.IsSkimmable);
    [JsonIgnore] public IEnumerable<FileReport> SkimFiles => Files.Where(f => f.IsSkimmable);
    [JsonIgnore] public IEnumerable<Finding> AllFindings => PullRequestFindings.Concat(Files.SelectMany(f => f.Findings));
}

public static class PrAnalyzer
{
    /// <param name="snapshot">The whole repo at the PR head, for repo-wide rules. Without it they only see the PR's files.</param>
    public static AnalysisReport Analyze(
        IEnumerable<ChangedFile> files,
        IEnumerable<IRule>? rules = null,
        PrBuddyConfig? config = null,
        IRepoSnapshot? snapshot = null,
        Func<ChangeHistory>? history = null)
    {
        config ??= PrBuddyConfig.Default;
        var ruleList = (rules ?? DefaultRules.Create(config)).ToList();
        var knownIds = ruleList.Select(r => r.Descriptor.Id).Append(SuppressionCheck.Descriptor.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        config.Validate(knownIds);

        var (ignored, kept) = files.ToList().Partition(f => config.IsIgnored(f.Path));
        var analyzed = kept.Select(f => new AnalyzedFile(f)).ToList();
        var context = new PullRequestContext(analyzed, snapshot, config.IsIgnored, history);

        var findings = new List<Finding>();
        foreach (var rule in ruleList)
        {
            var settings = config.For(rule.Descriptor.Id);
            if (!settings.Enabled) continue;
            try
            {
                var results = rule.Analyze(context);
                if (settings.Severity is { } severity) results = results.Select(f => f with { Severity = severity });
                findings.AddRange(results.ToList());
            }
            catch (Exception ex)
            {
                // One broken rule shouldn't take the whole report down with it.
                findings.Add(new Finding(rule.Descriptor.Id, Severity.Info, $"Rule failed and was skipped: {ex.Message}"));
            }
        }

        var (remaining, suppressed, problems) = SuppressionCheck.Apply(analyzed, findings, knownIds);
        var check = config.For(SuppressionCheck.Descriptor.Id);
        if (check.Enabled)
            remaining.AddRange(check.Severity is { } s ? problems.Select(p => p with { Severity = s }) : problems);
        findings = remaining;

        var byPath = findings.Where(f => f.Path is not null).ToLookup(f => f.Path!, StringComparer.Ordinal);
        var reports = analyzed
            .Select(file =>
            {
                var fileFindings = byPath[file.Path].OrderBy(f => f.Line ?? 0).ToList();
                return new FileReport(file.Path, file.File.OldPath, file.File.Status, file.Kind,
                    file.File.AddedLines.Count, file.File.DeletedLineCount,
                    RiskScorer.Score(file, fileFindings), file.SemanticDiff, fileFindings);
            })
            .OrderBy(r => r.IsSkimmable)
            .ThenByDescending(r => r.RiskScore)
            .ThenBy(r => r.Path, StringComparer.Ordinal)
            .ToList();

        return new AnalysisReport(reports, findings.Where(f => f.Path is null).ToList(), ignored.Select(f => f.Path).ToList(), suppressed);
    }

    static (List<T> Matching, List<T> Others) Partition<T>(this List<T> items, Func<T, bool> predicate) =>
        (items.Where(predicate).ToList(), items.Where(i => !predicate(i)).ToList());
}

/// <summary>A rough "where to look first" number. The weights are a starting point to tune, not science.</summary>
public static class RiskScorer
{
    public static int Score(AnalyzedFile file, IReadOnlyList<Finding> findings)
    {
        if (file.Kind == FileKind.Generated) return 0;

        var score = findings.Sum(f => f.Severity switch
        {
            Severity.Critical => 8,
            Severity.Warning => 4,
            _ => 1,
        });

        if (file.SemanticDiff is { } diff)
        {
            // Test methods are public by convention, so "public API" means nothing there.
            var apiMatters = file.Kind != FileKind.Test;
            var changes = diff.Changes.Sum(c => Weight(c, apiMatters));
            // Test changes count half, so a big new test file doesn't outrank the code it tests.
            score += file.Kind == FileKind.Test ? (changes + 1) / 2 : changes;
        }
        else
        {
            score += Math.Min(5, (file.File.AddedLines.Count + file.File.DeletedLineCount) / 20);
            if (file.Kind == FileKind.BuildConfig) score += 2;
        }

        return score;
    }

    static int Weight(MemberChange change, bool apiMatters)
    {
        var weight = change.Change switch
        {
            MemberChangeKind.SignatureChanged => 4,
            MemberChangeKind.BehaviorChanged => 3,
            MemberChangeKind.Added or MemberChangeKind.Removed => 2,
            MemberChangeKind.Renamed => 1,
            _ => 0,
        };
        return weight > 0 && apiMatters && change.IsPublicApi ? weight + 2 : weight;
    }
}
