using PrBuddy.Core;

namespace PrBuddy.Cli;

public sealed class CliOptions
{
    public const string Usage = """
        prbuddy: a deterministic sanity check for pull requests

        Usage: prbuddy [<base>] [<head>] [options]

          <base>   Branch or commit the PR targets (default: origin/main, origin/master, main or master)
          <head>   Branch or commit with the changes (default: HEAD)

        Options:
          -u, --uncommitted       Check the working tree (staged, unstaged and new files) instead
                                  of a commit, so you can review before committing
          -r, --repo <path>       Repository to analyze (default: current directory)
          -c, --config <path>     Config file (default: prbuddy.json as it is on the base branch)
              --json              Print the report as JSON
              --markdown          Print the report as Markdown, for a PR comment
              --sarif             Print the report as SARIF 2.1.0, for IDEs and code scanning
              --link-base <url>   Turn files and lines into links, e.g. https://github.com/o/r/blob/<sha>
              --online            Look new NuGet packages up on nuget.org (sends their names
                                  there; everything else stays offline)
              --fail-on <level>   Exit with code 1 if any finding is at least info, warning or critical
                                  (overrides failOn in the config)
              --list-rules        Show the built-in rules
              --explain <rule>    Why a rule exists, what to do about it, and its options
          -h, --help              Show this help

        Exit codes: 0 ok, 1 findings at or above --fail-on, 2 bad input or config.
        """;

    public string? BaseRef { get; private set; }
    public string HeadRef { get; private set; } = "HEAD";
    public string RepoPath { get; private set; } = ".";
    public string? ConfigPath { get; private set; }
    public string? LinkBase { get; private set; }
    public bool Uncommitted { get; private set; }
    public bool Online { get; private set; }
    public bool Json { get; private set; }
    public bool Markdown { get; private set; }
    public bool Sarif { get; private set; }
    public bool ListRules { get; private set; }
    public string? Explain { get; private set; }
    public bool Help { get; private set; }
    public Severity? FailOn { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();
        var positional = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    options.Help = true;
                    break;
                case "--json":
                    options.Json = true;
                    break;
                case "--online":
                    options.Online = true;
                    break;
                case "-u" or "--uncommitted":
                    options.Uncommitted = true;
                    break;
                case "--markdown":
                    options.Markdown = true;
                    break;
                case "--sarif":
                    options.Sarif = true;
                    break;
                case "-c" or "--config":
                    options.ConfigPath = Value(args, ref i);
                    break;
                case "--link-base":
                    options.LinkBase = Value(args, ref i);
                    break;
                case "--explain":
                    options.Explain = Value(args, ref i);
                    break;
                case "--list-rules":
                    options.ListRules = true;
                    break;
                case "-r" or "--repo":
                    options.RepoPath = Value(args, ref i);
                    break;
                case "--fail-on":
                    var level = Value(args, ref i);
                    options.FailOn = Enum.TryParse<Severity>(level, ignoreCase: true, out var severity) && Enum.IsDefined(severity)
                        ? severity
                        : throw new PrBuddyException($"--fail-on expects info, warning or critical, not '{level}'.");
                    break;
                case var arg when arg.StartsWith('-'):
                    throw new PrBuddyException($"Unknown option '{arg}'. Try --help.");
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        if (new[] { options.Json, options.Markdown, options.Sarif }.Count(x => x) > 1)
            throw new PrBuddyException("Pick one of --json, --markdown and --sarif.");
        if (options.Uncommitted && positional.Count > 1)
            throw new PrBuddyException("--uncommitted checks the working tree, so it takes a base ref but no head ref.");
        if (positional.Count > 2)
            throw new PrBuddyException("Expected at most two refs: <base> and <head>.");
        if (positional.Count > 0) options.BaseRef = positional[0];
        if (positional.Count > 1) options.HeadRef = positional[1];
        return options;
    }

    static string Value(string[] args, ref int i) =>
        ++i < args.Length ? args[i] : throw new PrBuddyException($"{args[i - 1]} needs a value.");
}
