using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrBuddy.Cli;
using PrBuddy.Core;
using PrBuddy.Core.Git;
using Spectre.Console;
using DefaultRules = PrBuddy.Core.Rules.DefaultRules;

Console.OutputEncoding = Encoding.UTF8;

try
{
    var options = CliOptions.Parse(args);
    if (options.Help)
    {
        Console.WriteLine(CliOptions.Usage);
        return 0;
    }
    if (options.ListRules)
    {
        ConsoleReport.WriteRules(DefaultRules.Create());
        return 0;
    }

    var (range, files) = GitDiffReader.Read(options.RepoPath, options.BaseRef, options.HeadRef);
    var report = PrAnalyzer.Analyze(files);

    if (options.Json)
        Console.WriteLine(JsonSerializer.Serialize(new { range, report }, JsonOutput.Options));
    else
        ConsoleReport.Write(range, report);

    return options.FailOn is { } threshold && report.AllFindings.Any(f => f.Severity >= threshold) ? 1 : 0;
}
catch (PrBuddyException ex)
{
    AnsiConsole.MarkupLineInterpolated($"[red]error:[/] {ex.Message}");
    return 2;
}

static class JsonOutput
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
}
