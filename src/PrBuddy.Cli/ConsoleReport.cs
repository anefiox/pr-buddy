using PrBuddy.Core;
using PrBuddy.Core.Git;
using PrBuddy.Core.Reporting;
using PrBuddy.Core.Rules;
using PrBuddy.Core.Semantics;
using Spectre.Console;

namespace PrBuddy.Cli;

internal sealed class ConsoleReport(IAnsiConsole console)
{
    public void Write(DiffRange range, AnalysisReport report, string? configSource)
    {
        console.Write(new Spectre.Console.Rule(
            $"[bold]PR Buddy[/] [grey]{Esc(range.BaseRef)}...{Esc(range.HeadRef)} ({range.MergeBaseSha[..7]}..{range.HeadSha[..7]})[/]")
        {
            Justification = Justify.Left,
        });
        console.MarkupLine($"[grey]{(configSource is null ? "default rules (no prbuddy.json on the base branch)" : "config: " + Esc(configSource))}[/]");

        if (report.Files.Count == 0)
        {
            console.MarkupLine($"[grey]{Esc(ReportText.NoChanges(report))}[/]");
            return;
        }

        WriteSummary(report);
        WritePullRequestFindings(report.PullRequestFindings);
        WriteReadingOrder(report);
        WriteSkimList(report);
        foreach (var file in report.ReviewFiles)
            WriteFileDetails(file);
        WriteSuppressed(report.Suppressed);
    }

    /// <summary>Shown, not hidden: a reviewer should see what was waved through and why.</summary>
    void WriteSuppressed(IReadOnlyList<SuppressedFinding> suppressed)
    {
        if (suppressed.Count == 0) return;
        console.MarkupLine($"[bold]Suppressed by comment[/] [grey]({suppressed.Count})[/]");
        foreach (var s in suppressed)
            console.MarkupLine($"  [grey]{Esc(ReportText.SuppressedLine(s))}[/]");
        console.WriteLine();
    }

    public void WriteExplanation(RuleDescriptor rule, RuleDoc doc)
    {
        console.Write(new Spectre.Console.Rule($"[bold]{rule.Id}[/] {Esc(rule.Title)}") { Justification = Justify.Left });
        console.MarkupLine($"[grey]{SeverityMarkup(rule.DefaultSeverity)} · {Esc(rule.Category)}[/]");
        console.WriteLine();
        console.MarkupLine("[bold]Why it matters[/]");
        console.WriteLine(doc.Why);
        console.WriteLine();
        console.MarkupLine("[bold]What to do[/]");
        console.WriteLine(doc.Fix);
        console.WriteLine();

        if (doc.Options.Count > 0)
        {
            var table = new Table { Border = TableBorder.Rounded, Title = new TableTitle("Options") };
            table.AddColumn("Option");
            table.AddColumn("Default");
            table.AddColumn("Meaning");
            foreach (var option in doc.Options) table.AddRow(Esc(option.Name), Esc(option.Default), Esc(option.Meaning));
            console.Write(table);
            console.WriteLine();
        }

        var example = doc.Options.Count > 0 ? $"{{ \"{doc.Options[0].Name}\": {doc.Options[0].Default} }}" : "\"info\"";
        console.MarkupLine("[bold]Configure or allow it[/]");
        console.MarkupLine($"  [grey]In one place:[/]  {Esc($"// prbuddy-ignore {rule.Id}: why this is fine here")}");
        console.MarkupLine($"  [grey]Repo-wide:[/]     {Esc($"prbuddy.json → \"rules\": {{ \"{rule.Id}\": \"off\" }}  or  {example}")}");
    }

    public void WriteRules(IEnumerable<IRule> rules)
    {
        var table = new Table { Border = TableBorder.Rounded };
        table.AddColumn("Id");
        table.AddColumn("Severity");
        table.AddColumn("Category");
        table.AddColumn("Rule");
        foreach (var d in rules.Select(r => r.Descriptor).Append(SuppressionCheck.Descriptor))
            table.AddRow(d.Id, SeverityMarkup(d.DefaultSeverity), Esc(d.Category), Esc(d.Title));
        console.Write(table);
    }

    void WriteSummary(AnalysisReport report)
    {
        var s = ReportText.Summarize(report);
        var ignored = (s.IgnoredFiles > 0 ? $"  ·  [grey]{s.IgnoredFiles} ignored by config[/]" : "")
                    + (s.Suppressed > 0 ? $"  ·  [grey]{s.Suppressed} suppressed by comment[/]" : "");
        console.MarkupLine(
            $"{s.Files} files  [green]+{s.AddedLines}[/] [red]-{s.DeletedLines}[/]  ·  [bold]{s.RealChanges}[/] real member changes" +
            $"  ·  {s.CosmeticFiles} cosmetic-only files  ·  {FindingCounts(report.AllFindings)}{ignored}");
        console.WriteLine();
    }

    void WritePullRequestFindings(IReadOnlyList<Finding> findings)
    {
        if (findings.Count == 0) return;
        console.MarkupLine("[bold]Whole PR[/]");
        foreach (var finding in findings)
            console.MarkupLine("  " + FindingLine(finding));
        console.WriteLine();
    }

    void WriteReadingOrder(AnalysisReport report)
    {
        var files = report.ReviewFiles.ToList();
        if (files.Count == 0)
        {
            console.MarkupLine("[green]Nothing needs a close read: every change is cosmetic or generated.[/]");
            console.WriteLine();
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
            table.AddRow((i + 1).ToString(), RiskMarkup(f.RiskScore), FileMarkup(f), Esc(ReportText.ChangeSummary(f)), FindingCounts(f.Findings));
        }
        console.Write(table);
        console.WriteLine();
    }

    void WriteSkimList(AnalysisReport report)
    {
        var skim = report.SkimFiles.ToList();
        if (skim.Count == 0) return;

        console.MarkupLine($"[bold]Skim only[/] [grey]({skim.Count})[/]");
        foreach (var f in skim)
            console.MarkupLine($"  [grey]{Esc(f.Path)} · {ReportText.SkimReason(f)}[/]");
        console.WriteLine();
    }

    void WriteFileDetails(FileReport file)
    {
        console.Write(new Spectre.Console.Rule($"[bold]{Esc(file.Path)}[/]")
        {
            Justification = Justify.Left,
            Style = Style.Parse("grey"),
        });
        if (file.OldPath is not null)
            console.MarkupLine($"  [grey]renamed from {Esc(file.OldPath)}[/]");

        if (file.SemanticDiff is { } diff)
        {
            if (diff.HasParseErrors)
                console.MarkupLine("  [yellow]This file has syntax errors, so the member list may be incomplete.[/]");
            foreach (var change in diff.Changes.Where(c => c.Change != MemberChangeKind.CosmeticOnly))
                console.MarkupLine("  " + ChangeLine(change, ReportText.ShowsPublicApi(file)));
            var cosmetic = diff.Changes.Count(c => c.Change == MemberChangeKind.CosmeticOnly);
            if (cosmetic > 0)
                console.MarkupLine($"  [grey]+ {cosmetic} member(s) with only formatting or comment changes[/]");
        }

        foreach (var finding in file.Findings)
            console.MarkupLine("  " + FindingLine(finding));
        console.WriteLine();
    }

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

    static string FindingCounts(IEnumerable<Finding> findings)
    {
        var counts = ReportText.CountFindings(findings);
        if (counts.Count == 0) return "[green]no findings[/]";
        return string.Join(", ", counts.Select(c => $"[{Color(c.Severity)}]{c.Label}[/]"));
    }

    static string Color(Severity severity) => severity switch
    {
        Severity.Critical => "red",
        Severity.Warning => "yellow",
        _ => "blue",
    };

    static string SeverityMarkup(Severity severity) => $"[{Color(severity)}]{severity.ToString().ToLowerInvariant()}[/]";

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
