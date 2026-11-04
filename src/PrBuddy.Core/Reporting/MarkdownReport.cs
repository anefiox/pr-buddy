using System.Text;
using PrBuddy.Core.Git;
using PrBuddy.Core.Semantics;

namespace PrBuddy.Core.Reporting;

/// <summary>The report as GitHub-flavored Markdown, for a PR comment or a job summary.</summary>
public static class MarkdownReport
{
    /// <summary>First line of every comment, so the action can find and update its own comment.</summary>
    public const string Marker = "<!-- prbuddy -->";

    /// <summary>GitHub rejects comments over 65,536 characters; leave room for the footer.</summary>
    public const int MaxLength = 60_000;

    /// <param name="linkBase">e.g. https://github.com/owner/repo/blob/&lt;sha&gt;. When set, files and lines become links.</param>
    public static string Render(DiffRange range, AnalysisReport report, string? linkBase = null, string? configSource = null)
    {
        var md = new StringBuilder();
        md.AppendLine(Marker);
        md.AppendLine("## PR Buddy");
        md.AppendLine();

        var summary = ReportText.Summarize(report);
        if (summary.Files == 0)
        {
            md.AppendLine(ReportText.NoChanges(report));
            return Footer(md, range, configSource).ToString();
        }

        md.Append($"**{summary.Files} files** · +{summary.AddedLines} −{summary.DeletedLines} · ");
        md.Append($"**{summary.RealChanges}** real member changes · {summary.CosmeticFiles} cosmetic-only files · ");
        md.AppendLine(FindingCounts(report.AllFindings));
        if (summary.IgnoredFiles > 0)
            md.AppendLine().AppendLine($"> {summary.IgnoredFiles} file(s) ignored by {PrBuddyConfigName}.");
        if (summary.Suppressed > 0)
            md.AppendLine().AppendLine($"> {summary.Suppressed} finding(s) suppressed by `prbuddy-ignore` comments (listed at the end).");
        md.AppendLine();

        if (report.PullRequestFindings.Count > 0)
        {
            md.AppendLine("### Whole PR");
            foreach (var finding in report.PullRequestFindings)
                md.AppendLine("- " + FindingText(finding, linkBase));
            md.AppendLine();
        }

        var review = report.ReviewFiles.ToList();
        if (review.Count == 0)
        {
            md.AppendLine("Nothing needs a close read: every change is cosmetic or generated.").AppendLine();
        }
        else
        {
            md.AppendLine("### Reading order");
            md.AppendLine("| # | Risk | File | What changed | Findings |");
            md.AppendLine("|--:|--:|---|---|---|");
            var rows = Math.Min(review.Count, MaxTableRows);
            for (var i = 0; i < rows; i++)
            {
                var f = review[i];
                var file = FileLink(f.Path, linkBase) + (f.Kind == FileKind.Test ? " (test)" : "");
                md.AppendLine($"| {i + 1} | {f.RiskScore} | {Cell(file)} | {Cell(Escape(ReportText.ChangeSummary(f)))} | {Cell(FindingCounts(f.Findings))} |");
            }
            md.AppendLine();
            if (review.Count > rows)
                md.AppendLine($"_…and {review.Count - rows} lower-risk file(s)._").AppendLine();
        }

        var skim = report.SkimFiles.ToList();
        if (skim.Count > 0)
        {
            md.AppendLine($"<details><summary>Skim only ({skim.Count})</summary>").AppendLine();
            foreach (var f in skim)
                md.AppendLine($"- {FileLink(f.Path, linkBase)}: {ReportText.SkimReason(f)}");
            md.AppendLine().AppendLine("</details>").AppendLine();
        }

        // File details go last and are the first thing dropped if the comment gets too long.
        var shown = 0;
        foreach (var file in review)
        {
            var section = FileSection(file, linkBase);
            if (md.Length + section.Length > MaxLength) break;
            md.Append(section);
            shown++;
        }
        if (shown < review.Count)
            md.AppendLine($"_Details for {review.Count - shown} more file(s) left out to fit GitHub's comment limit. Run `prbuddy` locally for the full report._").AppendLine();

        if (report.Suppressed.Count > 0 && md.Length < MaxLength)
        {
            md.AppendLine($"<details><summary>Suppressed by comment ({report.Suppressed.Count})</summary>").AppendLine();
            foreach (var s in report.Suppressed)
                md.AppendLine($"- **{s.Finding.RuleId}** {LineLink(s.Finding.Path!, s.Finding.Line ?? s.CommentLine, linkBase)} in `{s.Finding.Path}`: {Escape(s.Reason)}");
            md.AppendLine().AppendLine("</details>").AppendLine();
        }

        return Footer(md, range, configSource).ToString();
    }

    const string PrBuddyConfigName = Configuration.PrBuddyConfig.FileName;
    const int MaxTableRows = 100;

    static string FileSection(FileReport file, string? linkBase)
    {
        var md = new StringBuilder();
        md.AppendLine($"#### {FileLink(file.Path, linkBase)}");
        if (file.OldPath is not null) md.AppendLine($"Renamed from `{file.OldPath}`").AppendLine();

        if (file.SemanticDiff is { } diff)
        {
            if (diff.HasParseErrors)
                md.AppendLine("- ⚠️ This file has syntax errors, so the member list may be incomplete.");
            foreach (var change in diff.Changes.Where(c => c.Change != MemberChangeKind.CosmeticOnly))
                md.AppendLine("- " + ChangeText(file, change, linkBase));
            var cosmetic = diff.Changes.Count(c => c.Change == MemberChangeKind.CosmeticOnly);
            if (cosmetic > 0)
                md.AppendLine($"- _{cosmetic} member(s) with only formatting or comment changes_");
        }

        foreach (var finding in file.Findings)
            md.AppendLine("- " + FindingText(finding, linkBase));
        return md.AppendLine().ToString();
    }

    static string ChangeText(FileReport file, MemberChange change, string? linkBase)
    {
        var text = $"**{ReportText.ChangeWord(change.Change)}** `{change.DisplayName}`";
        if (ReportText.MoveText(change) is { } move) text += $" ({Escape(move)})";
        else if (change.OldDisplayName is { } old && old != change.DisplayName) text += $" (was `{old}`)";
        if (ReportText.ShowsPublicApi(file) && change.IsPublicApi) text += " · public API";
        if (change.Line is { } line) text += " · " + LineLink(file.Path, line, linkBase);
        return text;
    }

    static string FindingText(Finding finding, string? linkBase)
    {
        var icon = finding.Severity switch
        {
            Severity.Critical => "🛑",
            Severity.Warning => "⚠️",
            _ => "ℹ️",
        };
        var where = finding is { Path: { } path, Line: { } line } ? " " + LineLink(path, line, linkBase) : "";
        return $"{icon} **{finding.RuleId}**{where}: {Escape(finding.Message)}";
    }

    static string FindingCounts(IEnumerable<Finding> findings)
    {
        var counts = ReportText.CountFindings(findings);
        if (counts.Count == 0) return "✅ no findings";
        return string.Join(", ", counts.Select(c => c.Severity switch
        {
            Severity.Critical => "🛑 " + c.Label,
            Severity.Warning => "⚠️ " + c.Label,
            _ => "ℹ️ " + c.Label,
        }));
    }

    static string FileLink(string path, string? linkBase) =>
        linkBase is null ? $"`{path}`" : $"[`{path}`]({Url(linkBase, path)})";

    static string LineLink(string path, int line, string? linkBase) =>
        linkBase is null ? $"L{line}" : $"[L{line}]({Url(linkBase, path)}#L{line})";

    static string Url(string linkBase, string path) =>
        linkBase.TrimEnd('/') + "/" + string.Join("/", path.Split('/').Select(Uri.EscapeDataString));

    static StringBuilder Footer(StringBuilder md, DiffRange range, string? configSource)
    {
        var config = configSource is null ? "default rules" : $"config: {configSource}";
        return md.AppendLine($"<sub>`{range.BaseRef}...{range.HeadRef}` ({Short(range.MergeBaseSha)}..{Short(range.HeadSha)}) · {config} · deterministic, no AI</sub>");
    }

    static string Short(string sha) => sha.Length > 7 ? sha[..7] : sha;

    /// <summary>Messages quote code like &lt;NoWarn&gt;, which GitHub would otherwise swallow as HTML.</summary>
    static string Escape(string text) => text.Replace("<", "&lt;").Replace(">", "&gt;");

    static string Cell(string text) => text.Replace("|", "\\|");
}
