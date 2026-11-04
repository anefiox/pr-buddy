using PrBuddy.Core.Semantics;

namespace PrBuddy.Core.Reporting;

public sealed record ReportSummary(int Files, int AddedLines, int DeletedLines, int RealChanges, int CosmeticFiles, int IgnoredFiles);

/// <summary>Wording shared by every output format, so the console and the PR comment say the same thing.</summary>
public static class ReportText
{
    public static ReportSummary Summarize(AnalysisReport report) => new(
        report.Files.Count,
        report.Files.Sum(f => f.AddedLines),
        report.Files.Sum(f => f.DeletedLines),
        report.Files.Sum(f => f.SemanticDiff?.RealChangeCount ?? 0),
        report.Files.Count(f => f.Kind != FileKind.Generated && f.SemanticDiff is { IsCosmeticOnly: true }),
        report.IgnoredPaths.Count);

    /// <summary>Test methods are public by convention, so "public API" is noise there.</summary>
    public static bool ShowsPublicApi(FileReport file) => file.Kind != FileKind.Test;

    /// <summary>"1 behavior, 2 added (2 public API)", or a line count for non-C# files.</summary>
    public static string ChangeSummary(FileReport file)
    {
        var prefix = file.Status switch
        {
            ChangeStatus.Added => "new file: ",
            ChangeStatus.Deleted => "deleted: ",
            _ => "",
        };

        if (file.SemanticDiff is not { } diff)
            return $"{prefix}+{file.AddedLines} -{file.DeletedLines} lines";

        var real = diff.Changes.Where(c => c.Change != MemberChangeKind.CosmeticOnly).ToList();
        if (real.Count == 0) return prefix + "formatting only";

        var parts = real.GroupBy(c => c.Change).OrderBy(g => g.Key).Select(g => $"{g.Count()} {ChangeWord(g.Key)}");
        var api = ShowsPublicApi(file) ? real.Count(c => c.IsPublicApi) : 0;
        return prefix + string.Join(", ", parts) + (api > 0 ? $" ({api} public API)" : "");
    }

    public static string ChangeWord(MemberChangeKind kind) => kind switch
    {
        MemberChangeKind.BehaviorChanged => "behavior",
        MemberChangeKind.SignatureChanged => "signature",
        MemberChangeKind.Added => "added",
        MemberChangeKind.Removed => "removed",
        MemberChangeKind.Renamed => "renamed/moved",
        _ => "cosmetic",
    };

    public static string SkimReason(FileReport file) => file switch
    {
        { Kind: FileKind.Generated } => "generated",
        { OldPath: { } old, AddedLines: 0, DeletedLines: 0 } => $"renamed from {old}, contents unchanged",
        { OldPath: { } old } => $"renamed from {old}, formatting or comments only",
        _ => "formatting, comments or member order only",
    };

    /// <summary>"No changes to review", plus how many changes the config hid, so an all-ignored PR isn't mistaken for an empty one.</summary>
    public static string NoChanges(AnalysisReport report) => report.IgnoredPaths.Count == 0
        ? "No changes to review."
        : $"No changes to review: all {report.IgnoredPaths.Count} changed file(s) are ignored by {Configuration.PrBuddyConfig.FileName}.";

    /// <summary>Most severe first, only non-zero counts: [(Warning, 2, "2 warnings"), (Info, 1, "1 info")].</summary>
    public static IReadOnlyList<(Severity Severity, string Label)> CountFindings(IEnumerable<Finding> findings)
    {
        var counts = findings.GroupBy(f => f.Severity).ToDictionary(g => g.Key, g => g.Count());
        return new[] { Severity.Critical, Severity.Warning, Severity.Info }
            .Where(counts.ContainsKey)
            .Select(s => (s, Label(s, counts[s])))
            .ToList();
    }

    static string Label(Severity severity, int count) => severity switch
    {
        Severity.Warning => count == 1 ? "1 warning" : $"{count} warnings",
        Severity.Critical => $"{count} critical",
        _ => $"{count} info",
    };
}
