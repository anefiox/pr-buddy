using PrBuddy.Core;
using PrBuddy.Core.Git;
using PrBuddy.Core.Rules;
using PrBuddy.Core.Semantics;
using Spectre.Console;

namespace PrBuddy.Cli;

internal static class ConsoleReport
{
    public static void Write(DiffRange range, AnalysisReport report)
    {
        AnsiConsole.Write(new Spectre.Console.Rule(
            $"[bold]PR Buddy[/] [grey]{Esc(range.BaseRef)}...{Esc(range.HeadRef)} ({range.MergeBaseSha[..7]}..{range.HeadSha[..7]})[/]")
        {
            Justification = Justify.Left,
        });

        if (report.Files.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]No changes between these refs.[/]");
            return;
        }

        WriteSummary(report);
        WritePullRequestFindings(report.PullRequestFindings);
        WriteReadingOrder(report);
        WriteSkimList(report);
        foreach (var file in report.ReviewFiles)
            WriteFileDetails(file);
    }

    public static void WriteRules(IEnumerable<IRule> rules)
    {
        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Id");
        table.AddColumn("Severity");
        table.AddColumn("Category");
        table.AddColumn("Rule");
        foreach (var d in rules.Select(r => r.Descriptor))
            table.AddRow(d.Id, SeverityMarkup(d.DefaultSeverity), Esc(d.Category), Esc(d.Title));
        AnsiConsole.Write(table);
    }

    static void WriteSummary(AnalysisReport report)
    {
        var added = report.Files.Sum(f => f.AddedLines);
        var deleted = report.Files.Sum(f => f.DeletedLines);
        var real = report.Files.Sum(f => f.SemanticDiff?.RealChangeCount ?? 0);
        var cosmetic = report.Files.Count(f => f.Kind != FileKind.Generated && f.SemanticDiff is { IsCosmeticOnly: true });

        AnsiConsole.MarkupLine(
            $"{report.Files.Count} files  [green]+{added}[/] [red]-{deleted}[/]  ·  [bold]{real}[/] real member changes" +
            $"  ·  {cosmetic} cosmetic-only files  ·  {FindingCounts(report.AllFindings.ToList())}");
        AnsiConsole.WriteLine();
    }

    static void WritePullRequestFindings(IReadOnlyList<Finding> findings)
    {
        if (findings.Count == 0) return;
        AnsiConsole.MarkupLine("[bold]Whole PR[/]");
        foreach (var finding in findings)
            AnsiConsole.MarkupLine("  " + FindingLine(finding));
        AnsiConsole.WriteLine();
    }

    static void WriteReadingOrder(AnalysisReport report)
    {
        var files = report.ReviewFiles.ToList();
        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]Nothing needs a close read: every change is cosmetic or generated.[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var table = new Table { Border = TableBorder.Rounded, Title = new TableTitle("Reading order") };
        table.AddColumn("#");
        table.AddColumn(new TableColumn("Risk") { Alignment = Justify.Right });
        table.AddColumn("File");
        table.AddColumn("What changed");
        table.AddColumn("Findings");

        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            table.AddRow((i + 1).ToString(), RiskMarkup(f.RiskScore), FileMarkup(f), Esc(ChangeSummary(f)), FindingCounts(f.Findings));
        }
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    static void WriteSkimList(AnalysisReport report)
    {
        var skim = report.SkimFiles.ToList();
        if (skim.Count == 0) return;

        AnsiConsole.MarkupLine($"[bold]Skim only[/] [grey]({skim.Count})[/]");
        foreach (var f in skim)
        {
            var reason = f.Kind == FileKind.Generated ? "generated" : "formatting, comments or member order only";
            AnsiConsole.MarkupLine($"  [grey]{Esc(f.Path)} · {reason}[/]");
        }
        AnsiConsole.WriteLine();
    }

    static void WriteFileDetails(FileReport file)
    {
        AnsiConsole.Write(new Spectre.Console.Rule($"[bold]{Esc(file.Path)}[/]")
        {
            Justification = Justify.Left,
            Style = Style.Parse("grey"),
        });
        if (file.OldPath is not null)
            AnsiConsole.MarkupLine($"  [grey]renamed from {Esc(file.OldPath)}[/]");

        if (file.SemanticDiff is { } diff)
        {
            if (diff.HasParseErrors)
                AnsiConsole.MarkupLine("  [yellow]This file has syntax errors, so the member list may be incomplete.[/]");
            foreach (var change in diff.Changes.Where(c => c.Change != MemberChangeKind.CosmeticOnly))
                AnsiConsole.MarkupLine("  " + ChangeLine(change, showApi: file.Kind != FileKind.Test));
            var cosmetic = diff.Changes.Count(c => c.Change == MemberChangeKind.CosmeticOnly);
            if (cosmetic > 0)
                AnsiConsole.MarkupLine($"  [grey]+ {cosmetic} member(s) with only formatting or comment changes[/]");
        }

        foreach (var finding in file.Findings)
            AnsiConsole.MarkupLine("  " + FindingLine(finding));
        AnsiConsole.WriteLine();
    }

    static string ChangeSummary(FileReport file)
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
        var api = file.Kind == FileKind.Test ? 0 : real.Count(c => c.IsPublicApi);
        return prefix + string.Join(", ", parts) + (api > 0 ? $" ({api} public API)" : "");
    }

    static string ChangeWord(MemberChangeKind kind) => kind switch
    {
        MemberChangeKind.BehaviorChanged => "behavior",
        MemberChangeKind.SignatureChanged => "signature",
        MemberChangeKind.Added => "added",
        MemberChangeKind.Removed => "removed",
        MemberChangeKind.Renamed => "renamed/moved",
        _ => "cosmetic",
    };

    static string ChangeLine(MemberChange change, bool showApi)
    {
        var label = change.Change switch
        {
            MemberChangeKind.BehaviorChanged => "[yellow]behavior [/]",
            MemberChangeKind.SignatureChanged => "[orange1]signature[/]",
            MemberChangeKind.Added => "[green]added    [/]",
            MemberChangeKind.Removed => "[red]removed  [/]",
            MemberChangeKind.Renamed => "[blue]renamed  [/]",
            _ => "[grey]cosmetic [/]",
        };

        var line = $"{label} {Esc(change.DisplayName)}";
        if (change.OldDisplayName is { } old && old != change.DisplayName) line += $" [grey]was {Esc(old)}[/]";
        if (showApi && change.IsPublicApi) line += " [red]public API[/]";
        if (change.Line is { } number) line += $" [grey]L{number}[/]";
        return line;
    }

    static string FindingLine(Finding finding)
    {
        var icon = finding.Severity switch
        {
            Severity.Critical => "[red]✖[/]",
            Severity.Warning => "[yellow]⚠[/]",
            _ => "[blue]ℹ[/]",
        };
        var where = finding.Line is { } line ? $"L{line} " : "";
        return $"{icon} [grey]{finding.RuleId}[/] {where}{Esc(finding.Message)}";
    }

    static string FindingCounts(IReadOnlyCollection<Finding> findings)
    {
        if (findings.Count == 0) return "[green]no findings[/]";
        var parts = new List<string>();
        AddCount(Severity.Critical, "critical", "red");
        AddCount(Severity.Warning, "warning", "yellow");
        AddCount(Severity.Info, "info", "blue");
        return string.Join(", ", parts);

        void AddCount(Severity severity, string word, string color)
        {
            var count = findings.Count(f => f.Severity == severity);
            if (count > 0) parts.Add($"[{color}]{count} {word}{(count > 1 && severity == Severity.Warning ? "s" : "")}[/]");
        }
    }

    static string SeverityMarkup(Severity severity) => severity switch
    {
        Severity.Critical => "[red]critical[/]",
        Severity.Warning => "[yellow]warning[/]",
        _ => "[blue]info[/]",
    };

    static string RiskMarkup(int score) => score switch
    {
        >= 15 => $"[red]{score}[/]",
        >= 6 => $"[yellow]{score}[/]",
        _ => $"[grey]{score}[/]",
    };

    static string FileMarkup(FileReport file)
    {
        var markup = Esc(file.Path);
        if (file.Kind == FileKind.Test) markup += " [grey](test)[/]";
        if (file.Status == ChangeStatus.Renamed) markup += " [blue](renamed)[/]";
        return markup;
    }

    static string Esc(string text) => Markup.Escape(text);
}
