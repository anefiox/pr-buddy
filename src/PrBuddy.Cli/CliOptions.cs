using PrBuddy.Core;

namespace PrBuddy.Cli;

internal sealed class CliOptions
{
    public const string Usage = """
        prbuddy: a deterministic sanity check for pull requests

        Usage: prbuddy [<base>] [<head>] [options]

          <base>   Branch or commit the PR targets (default: origin/main, origin/master, main or master)
          <head>   Branch or commit with the changes (default: HEAD)

        Options:
          -r, --repo <path>       Repository to analyze (default: current directory)
              --json              Print the report as JSON
              --fail-on <level>   Exit with code 1 if any finding is at least info, warning or critical
              --list-rules        Show the built-in rules
          -h, --help              Show this help
        """;

    public string? BaseRef { get; private set; }
    public string HeadRef { get; private set; } = "HEAD";
    public string RepoPath { get; private set; } = ".";
    public bool Json { get; private set; }
    public bool ListRules { get; private set; }
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

        if (positional.Count > 2)
            throw new PrBuddyException("Expected at most two refs: <base> and <head>.");
        if (positional.Count > 0) options.BaseRef = positional[0];
        if (positional.Count > 1) options.HeadRef = positional[1];
        return options;
    }

    static string Value(string[] args, ref int i) =>
        ++i < args.Length ? args[i] : throw new PrBuddyException($"{args[i - 1]} needs a value.");
}
