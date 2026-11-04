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

    /// <summary>"0.1.0", without the "+commit" suffix the SDK adds to the informational version.</summary>
    public static string VersionText =>
        (System.Reflection.Assembly.GetExecutingAssembly()
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "unknown")
        .Split('+')[0];

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
            if (options.Init)
                return Init(options, stdout);
            if (options.Version)
            {
                stdout.WriteLine($"prbuddy {VersionText}");
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
            config = ApplyRuleFilter(options, config, rules.Select(r => r.Descriptor.Id).Append(SuppressionCheck.Descriptor.Id).ToList());
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

    /// <summary>--only / --skip switch rules off on top of prbuddy.json; severities and options still apply.</summary>
    static PrBuddyConfig ApplyRuleFilter(CliOptions options, PrBuddyConfig config, IReadOnlyList<string> allIds)
    {
        var named = options.Only.Concat(options.Skip).ToList();
        var unknown = named.Where(id => !allIds.Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0)
            throw new PrBuddyException($"Unknown rule(s): {string.Join(", ", unknown)}. Run prbuddy --list-rules.");

        if (options.Only.Count > 0)
            return config.WithRulesDisabled(allIds.Where(id => !options.Only.Contains(id, StringComparer.OrdinalIgnoreCase)));
        return options.Skip.Count > 0 ? config.WithRulesDisabled(options.Skip) : config;
    }

    /// <summary>Writes a starter prbuddy.json at the repository root. Never overwrites without --force.</summary>
    static int Init(CliOptions options, TextWriter stdout)
    {
        var root = GitDiffReader.WorkingDirectory(options.RepoPath);
        var path = Path.Combine(root, PrBuddyConfig.FileName);
        if (File.Exists(path) && !options.Force)
            throw new PrBuddyException($"{path} already exists. Use --force to replace it.");

        File.WriteAllText(path, ConfigTemplate.Text + Environment.NewLine);
        stdout.WriteLine($"Created {path}");
        stdout.WriteLine("Every setting in it is commented out, so it changes nothing until you edit it.");
        stdout.WriteLine("Commit it to your main branch for it to apply to PRs. To try it now: prbuddy --config prbuddy.json");
        return Ok;
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
