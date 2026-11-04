using System.Text.RegularExpressions;

namespace PrBuddy.Core.Rules.Config;

/// <summary>PRB203: project or analyzer settings that switch checks off for the whole project.</summary>
public sealed class WeakenedChecksRule : RuleBase
{
    const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    static readonly (Regex Pattern, string Message)[] Checks =
    [
        (new(@"<Nullable>\s*disable", Options), "Nullable reference types disabled."),
        (new(@"<TreatWarningsAsErrors>\s*false", Options), "Warnings no longer fail the build."),
        (new(@"<NoWarn>", Options), "Warnings suppressed for the whole project."),
        (new(@"<WarningLevel>\s*0", Options), "Compiler warnings turned off."),
        (new(@"<(RunAnalyzers|RunAnalyzersDuringBuild|EnableNETAnalyzers)>\s*false", Options), "Code analyzers turned off."),
        (new(@"<AnalysisLevel>\s*none", Options), "Code analysis turned off."),
        (new(@"severity\s*=\s*(none|silent)", Options), "Analyzer rule silenced."),
        (new(@"Action\s*=\s*""None""", Options), "Analyzer rule disabled in a ruleset."),
    ];

    public override RuleDescriptor Descriptor { get; } = new("PRB203", "Checks weakened project-wide", Severity.Warning, "Config");

    public override IEnumerable<Finding> Analyze(PullRequestContext context)
    {
        var files = context.Files.Where(f => FileClassifier.IsProjectFile(f.Path) || FileClassifier.IsBuildConfig(f.Path));
        foreach (var file in files)
        {
            foreach (var (line, text) in file.AddedLineTexts())
            {
                var check = Checks.FirstOrDefault(c => c.Pattern.IsMatch(text));
                if (check.Pattern is not null)
                    yield return Report(file.Path, line, $"{check.Message} ({SyntaxHelpers.Truncate(text, 60)})");
            }
        }
    }
}
