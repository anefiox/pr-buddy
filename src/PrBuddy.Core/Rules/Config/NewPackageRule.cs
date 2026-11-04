using System.Text.RegularExpressions;

namespace PrBuddy.Core.Rules.Config;

/// <summary>
/// PRB202: a NuGet package the project didn't depend on before. Version bumps are ignored.
/// Offline for now. A later step could ask nuget.org for download counts and publish dates.
/// </summary>
public sealed partial class NewPackageRule : RuleBase
{
    public override RuleDescriptor Descriptor { get; } = new("PRB202", "New package dependency", Severity.Warning, "Dependencies");

    public override IEnumerable<Finding> Analyze(PullRequestContext context)
    {
        foreach (var file in context.Files.Where(f => FileClassifier.IsProjectFile(f.Path) && f.File.NewContent is not null))
        {
            var existing = Packages(file.File.OldContent);
            foreach (var (line, text) in file.AddedLineTexts())
            {
                foreach (Match match in PackageReference().Matches(text))
                {
                    var name = match.Groups[1].Value;
                    if (!existing.Contains(name))
                        yield return Report(file.Path, line,
                            $"New package dependency '{name}'. Check the spelling, that it's the package you expect, and that it's actually needed.");
                }
            }
        }
    }

    static HashSet<string> Packages(string? content) =>
        content is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : PackageReference().Matches(content).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"<Package(?:Reference|Version)\b[^>]*?\bInclude\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PackageReference();
}
