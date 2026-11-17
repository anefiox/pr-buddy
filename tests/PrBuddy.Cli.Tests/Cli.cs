using Spectre.Console;

namespace PrBuddy.Cli.Tests;

/// <param name="Console">What the pretty console output showed (no colors).</param>
/// <param name="Out">Plain stdout: help, JSON, Markdown.</param>
public sealed record CliResult(int Exit, string Console, string Out, string Err);

public static class Cli
{
    public static CliResult Run(params string[] args) => Run(null, args);

    public static CliResult Run(PrBuddy.Core.Packages.IPackageRegistry? registry, params string[] args)
    {
        var console = new StringWriter();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var ansi = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(console),
            // Spectre's CI enrichers (GitHub Actions, Azure Pipelines...) switch ANSI back on after these
            // settings are read, which puts escape codes in the output and breaks every text assertion.
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
        });
        ansi.Profile.Width = 250; // wide enough that table cells don't wrap mid-word

        var exit = CliApp.Run(args, ansi, stdout, stderr, registry);
        return new CliResult(exit, console.ToString(), stdout.ToString(), stderr.ToString());
    }
}
