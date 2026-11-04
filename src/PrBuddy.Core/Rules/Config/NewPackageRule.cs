using System.Text.RegularExpressions;
using PrBuddy.Core.Packages;

namespace PrBuddy.Core.Rules.Config;

/// <summary>
/// PRB202: a NuGet package the project didn't depend on before. Version bumps are ignored.
///
/// Offline (the default), every new package is a warning to check by hand, and names one keystroke
/// away from a popular package say which one they look like. With a registry (--online),
/// well-known packages drop to info and the ones that need a look say why: not on nuget.org (a
/// typo, a private feed, or a package that doesn't exist at all) or hardly used.
/// </summary>
public sealed partial class NewPackageRule(IPackageRegistry? registry = null) : RuleBase
{
    /// <summary>Fewer total downloads than this and a package deserves a second look.</summary>
    public const long FewDownloads = 10_000;

    public override RuleDescriptor Descriptor { get; } = new("PRB202", "New package dependency", Severity.Warning, "Dependencies");

    public override IEnumerable<Finding> Analyze(PullRequestContext context)
    {
        foreach (var file in context.Files.Where(f => FileClassifier.IsProjectFile(f.Path) && f.File.NewContent is not null))
        {
            var lines = file.AddedLineTexts().ToList();
            int? LineOf(string package) =>
                lines.FirstOrDefault(l => PackageReference().Matches(l.Text).Any(m => Same(m.Groups[1].Value, package))).Line is var n and > 0 ? n : null;

            var existing = Packages(file.File.OldContent);
            var added = lines
                .SelectMany(l => PackageReference().Matches(l.Text).Select(m => m.Groups[1].Value))
                .Where(name => !existing.Contains(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (added.Count == 0) continue;

            var unremarkable = new List<string>(); // nothing specific to say: grouped into one finding
            foreach (var name in added)
            {
                var info = registry?.Lookup(name);
                var lookalike = PackageLookalikes.LookalikeOf(name);
                var typo = lookalike is null ? "" : $" It's one keystroke away from the popular '{lookalike}': is it a typo?";

                // A package that's popular in its own right isn't a typo, whatever its name.
                if (info is { Exists: true, TotalDownloads: >= FewDownloads })
                {
                    unremarkable.Add($"{info.Id} ({Downloads(info.TotalDownloads)} downloads{(info.Verified ? ", verified owner" : "")})");
                    continue;
                }

                var message = (registry, info) switch
                {
                    (null, _) when lookalike is not null => $"New package '{name}'.{typo}",
                    (null, _) => null,
                    (_, null) => $"New package dependency '{name}'. Couldn't reach nuget.org to check it, so check the spelling and that it's the package you expect.{typo}",
                    (_, { Exists: false }) => $"New package '{name}' isn't on nuget.org. Fine if it comes from a private feed; otherwise it's a typo or a package that doesn't exist.{typo}",
                    (_, var known) => $"New package '{known.Id}' has only {Downloads(known.TotalDownloads)} downloads on nuget.org. Check it's the package you meant and that you trust the author.{typo}",
                };

                if (message is not null) yield return Report(file.Path, LineOf(name), message);
                else unremarkable.Add(name);
            }

            if (unremarkable.Count == 0) continue;
            var first = LineOf(unremarkable[0].Split(' ')[0]);
            if (registry is not null)
                yield return Report(file.Path, first, $"New package(s) from nuget.org: {string.Join(", ", unremarkable)}.", Severity.Info);
            else if (file.File.Status == ChangeStatus.Added)
                // Offline, a brand-new project gets one finding, not one per package of boilerplate.
                yield return Report(file.Path, first,
                    $"New project with {unremarkable.Count} package(s): {string.Join(", ", unremarkable)}. Check each is the package you expect.");
            else
                foreach (var name in unremarkable)
                    yield return Report(file.Path, LineOf(name),
                        $"New package dependency '{name}'. Check the spelling, that it's the package you expect, and that it's actually needed.");
        }
    }

    public static string Downloads(long count) => count switch
    {
        >= 1_000_000_000 => $"{count / 1_000_000_000.0:0.#}B",
        >= 1_000_000 => $"{count / 1_000_000.0:0.#}M",
        >= 1_000 => $"{count / 1_000.0:0.#}K",
        _ => count.ToString(),
    };

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static HashSet<string> Packages(string? content) =>
        content is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : PackageReference().Matches(content).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"<Package(?:Reference|Version)\b[^>]*?\bInclude\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex PackageReference();
}
