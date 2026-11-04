namespace PrBuddy.Core.Rules.Structure;

/// <summary>
/// PRB303: most of the PR is in one area, but a few changes land somewhere else ("fix login bug"
/// that also edits billing). Those strays are easy to miss in review and often unintended.
///
/// An area is a file's first two folders (src/Auth/Login.cs → src/Auth). Only production C# counts:
/// tests naturally live elsewhere, and build files are PRB201's job.
/// </summary>
/// <param name="focusShare">The main area must hold at least this share of changed lines ("PRB303": { "focusShare": 0.7 }).</param>
public sealed class ScopeCreepRule(double focusShare = ScopeCreepRule.DefaultFocusShare) : RuleBase
{
    public const double DefaultFocusShare = 0.7;
    const int MinFiles = 3;
    const int MaxStrays = 3;
    /// <summary>Changed together in at least this many recent commits: a usual partner, not a stray.</summary>
    public const int UsualPartnerCommits = 3;
    /// <summary>An area that has changed alongside this many different areas is shared code, a partner of anything.</summary>
    public const int SharedAreaPartners = 3;

    public override RuleDescriptor Descriptor { get; } = new("PRB303", "Changes outside the PR's main area", Severity.Info, "Structure");

    public override IEnumerable<Finding> Analyze(PullRequestContext context)
    {
        var files = context.CSharpFiles
            .Where(f => f.Kind == FileKind.Code)
            .Select(f => (f.Path, Area: AreaOf(f.Path), Churn: f.File.AddedLines.Count + f.File.DeletedLineCount))
            .Where(f => f.Churn > 0)
            .ToList();
        if (files.Count < MinFiles) yield break;

        var total = files.Sum(f => f.Churn);
        var main = files.GroupBy(f => f.Area).Select(g => (Area: g.Key, Churn: g.Sum(f => f.Churn))).MaxBy(a => a.Churn);
        var share = (double)main.Churn / total;
        if (share < focusShare) yield break; // spread out on purpose; no single "main" area

        var strays = files.Where(f => f.Area != main.Area).OrderByDescending(f => f.Churn).ToList();
        if (strays.Count is 0 or > MaxStrays) yield break;

        // Areas that have often changed together before (a feature and its shared library, a game
        // and its test harness) are partners, not strays. Only loads history if there's a candidate.
        var partners = strays.Select(s => s.Area).Distinct()
            .Where(area => context.History.CoChanges(main.Area, area, AreaOf) >= UsualPartnerCommits
                        || context.History.DistinctPartners(area, AreaOf) >= SharedAreaPartners)
            .ToHashSet(StringComparer.Ordinal);
        strays = strays.Where(s => !partners.Contains(s.Area)).ToList();
        if (strays.Count == 0) yield break;

        var list = string.Join(", ", strays.Select(s => $"{s.Path} ({s.Churn} line{(s.Churn == 1 ? "" : "s")})"));
        yield return Report(path: null, line: null,
            message: $"{Reporting.Numbers.Percent(share)} of the changed code is in {main.Area}, but it also touches {list}. Are those changes meant to be in this PR?");
    }

    public static string AreaOf(string path)
    {
        var folders = path.Replace('\\', '/').Split('/')[..^1];
        return folders.Length == 0 ? "(root)" : string.Join("/", folders.Take(2));
    }
}
