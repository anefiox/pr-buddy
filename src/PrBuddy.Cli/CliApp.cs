using System.Text.Json;
using System.Text.Json.Serialization;
using PrBuddy.Core;
using PrBuddy.Core.Configuration;
using PrBuddy.Core.Git;
using PrBuddy.Core.Packages;
using PrBuddy.Core.Reporting;
using Spectre.Console;
using DefaultRules = PrBuddy.Core.Rules.DefaultRules;

namespace PrBuddy.Cli;

/// <summary>The whole command, with its outputs passed in so tests can run it in-process.</summary>
public static class CliApp
{
    public const int Ok = 0;
    public const int FindingsAtThreshold = 1;
    public const int BadInput = 2;

    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <param name="console">Pretty output (tables, colors).</param>
    /// <param name="stdout">Plain output: help, JSON, Markdown.</param>
    /// <param name="stderr">Errors, kept out of stdout so they never end up inside a redirected report.</param>
    /// <param name="registry">Used for --online. Null means the real nuget.org (tests pass a fake).</param>
    public static int Run(string[] args, IAnsiConsole console, TextWriter stdout, TextWriter stderr, IPackageRegistry? registry = null)
    {
        try
        {
            var options = CliOptions.Parse(args);
            if (options.Help)
            {
                stdout.WriteLine(CliOptions.Usage);
                return Ok;
            }
            if (options.Explain is { } ruleId)
            {
                var descriptor = DefaultRules.Create().Select(r => r.Descriptor).Append(SuppressionCheck.Descriptor)
                    .FirstOrDefault(d => string.Equals(d.Id, ruleId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new PrBuddyException($"There's no rule '{ruleId}'. Run prbuddy --list-rules.");
                new ConsoleReport(console).WriteExplanation(descriptor, PrBuddy.Core.Rules.RuleDocs.For(descriptor.Id)!);
                return Ok;
            }
            if (options.ListRules)
            {
                new ConsoleReport(console).WriteRules(DefaultRules.Create());
                return Ok;
            }

            var (range, files) = options.Uncommitted
                ? GitDiffReader.ReadUncommitted(options.RepoPath, options.BaseRef)
                : GitDiffReader.Read(options.RepoPath, options.BaseRef, options.HeadRef);
            var (config, configSource) = LoadConfig(options, range);
            using IDisposable snapshotLifetime = options.Uncommitted
                ? GitDiffReader.OpenWorkingTreeSnapshot(options.RepoPath)
                : GitDiffReader.OpenSnapshot(options.RepoPath, range.HeadSha);
            var snapshot = (IRepoSnapshot)snapshotLifetime;
            using var http = options.Online && registry is null ? new HttpClient { Timeout = TimeSpan.FromSeconds(10) } : null;
            if (http is not null) registry = new NuGetOrgRegistry(http);
            var rules = DefaultRules.Create(config, options.Online ? registry : null);
            var report = PrAnalyzer.Analyze(files, rules, config, snapshot,
                history: () => GitDiffReader.ReadHistory(options.RepoPath, range.MergeBaseSha));

            if (options.Json)
                stdout.WriteLine(JsonSerializer.Serialize(new { range, configSource, report }, JsonOptions));
            else if (options.Sarif)
                stdout.WriteLine(SarifReport.Render(report, rules.Select(r => r.Descriptor).Append(SuppressionCheck.Descriptor)));
            else if (options.Markdown)
                stdout.Write(MarkdownReport.Render(range, report, options.LinkBase, configSource));
            else
                new ConsoleReport(console).Write(range, report, configSource);

            var failOn = options.FailOn ?? config.FailOn;
            return failOn is { } threshold && report.AllFindings.Any(f => f.Severity >= threshold) ? FindingsAtThreshold : Ok;
        }
        catch (PrBuddyException ex)
        {
            stderr.WriteLine($"error: {ex.Message}");
            return BadInput;
        }
    }

    /// <summary>
    /// --config wins. Otherwise prbuddy.json is read from the merge base, not from the PR, so a PR
    /// can't switch off the checks that would have flagged it. Changing it shows up as PRB201.
    /// </summary>
    static (PrBuddyConfig Config, string? Source) LoadConfig(CliOptions options, DiffRange range)
    {
        if (options.ConfigPath is { } path)
        {
            if (!File.Exists(path)) throw new PrBuddyException($"Config file '{path}' not found.");
            return (PrBuddyConfig.Parse(File.ReadAllText(path), path), path);
        }

        var source = $"{PrBuddyConfig.FileName} @ {range.MergeBaseSha[..7]} (base)";
        var json = GitDiffReader.ReadFile(options.RepoPath, range.MergeBaseSha, PrBuddyConfig.FileName);
        return json is null ? (PrBuddyConfig.Default, null) : (PrBuddyConfig.Parse(json, source), source);
    }
}
