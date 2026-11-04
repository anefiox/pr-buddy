using System.Text.Json;
using System.Text.Json.Serialization;
using PrBuddy.Core;
using PrBuddy.Core.Configuration;
using PrBuddy.Core.Git;
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
    public static int Run(string[] args, IAnsiConsole console, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            var options = CliOptions.Parse(args);
            if (options.Help)
            {
                stdout.WriteLine(CliOptions.Usage);
                return Ok;
            }
            if (options.ListRules)
            {
                new ConsoleReport(console).WriteRules(DefaultRules.Create());
                return Ok;
            }

            var (range, files) = GitDiffReader.Read(options.RepoPath, options.BaseRef, options.HeadRef);
            var (config, configSource) = LoadConfig(options, range);
            using var snapshot = GitDiffReader.OpenSnapshot(options.RepoPath, range.HeadSha);
            var report = PrAnalyzer.Analyze(files, config: config, snapshot: snapshot);

            if (options.Json)
                stdout.WriteLine(JsonSerializer.Serialize(new { range, configSource, report }, JsonOptions));
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
