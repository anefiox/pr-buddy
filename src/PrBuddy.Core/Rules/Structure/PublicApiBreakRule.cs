using System.Text.RegularExpressions;
using PrBuddy.Core.Semantics;

namespace PrBuddy.Core.Rules.Structure;

/// <summary>
/// PRB304: a public member of a public type in a library was removed, renamed or given different
/// parameters. Code outside this repository that calls it stops compiling (or, for a binary, fails
/// at run time). Apps (Exe, web and worker projects, or IsPackable=false) are skipped: nothing
/// outside builds against them.
/// </summary>
public sealed partial class PublicApiBreakRule : RuleBase
{
    public override RuleDescriptor Descriptor { get; } = new("PRB304", "Public API changed incompatibly", Severity.Info, "Structure");

    /// <summary>More files than this and the PR is a deliberate API reshuffle: one finding says it all.</summary>
    const int MaxFileFindings = 3;

    public override IEnumerable<Finding> Analyze(PullRequestContext context)
    {
        var projects = context.Snapshot.ProjectPaths;
        var perFile = new List<(AnalyzedFile File, MemberChange[] Breaks)>();
        foreach (var file in context.CSharpFiles.Where(f => !f.IsTestFile && !f.IsGenerated && f.SemanticDiff is not null))
        {
            var breaks = file.SemanticDiff!.Changes.Where(c => c.IsPublicApi && c.Change switch
            {
                MemberChangeKind.Removed => true,
                // Same display name means it only moved namespace, or only attributes, modifiers or the return type changed.
                MemberChangeKind.Renamed or MemberChangeKind.SignatureChanged => c.OldDisplayName is not null && c.OldDisplayName != c.DisplayName,
                _ => false,
            }).ToArray();
            if (breaks.Length > 0 && IsLibrary(context.Snapshot, projects, file.Path)) perFile.Add((file, breaks));
        }

        if (perFile.Count > MaxFileFindings)
        {
            var members = perFile.Sum(f => f.Breaks.Length);
            yield return Report((string?)null, null,
                $"{members} public members across {perFile.Count} files were removed, renamed or changed signature, " +
                $"starting with {string.Join(", ", perFile.Take(3).Select(f => System.IO.Path.GetFileName(f.File.Path)))}. " +
                "Anything built against the old API breaks. If this package has outside users, plan the version bump and release notes.");
            yield break;
        }

        foreach (var (file, breaks) in perFile)
        {
            var described = breaks.Take(4).Select(c => c.Change == MemberChangeKind.Removed
                ? $"removed {c.DisplayName}"
                : $"{c.OldDisplayName} became {c.DisplayName}");
            yield return Report(file.Path, breaks.Select(c => c.Line).FirstOrDefault(l => l is not null),
                $"{(breaks.Length == 1 ? "A public member changed" : $"{breaks.Length} public members changed")} in a way that breaks callers " +
                $"outside this repo: {string.Join("; ", described)}{(breaks.Length > 4 ? "; ..." : "")}. " +
                "If others build against this, keep the old signature as an overload (perhaps [Obsolete]) or call out the break in the release notes.");
        }
    }

    /// <summary>
    /// The nearest project above the file decides. A file with no project found counts as a library,
    /// so the rule still reports when project files aren't available.
    /// </summary>
    static bool IsLibrary(IRepoSnapshot snapshot, IReadOnlyList<string> projects, string path)
    {
        var dir = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
        while (true)
        {
            var prefix = dir.Length == 0 ? "" : dir + "/";
            var project = projects.FirstOrDefault(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
                p.StartsWith(prefix, StringComparison.Ordinal) && p.IndexOf('/', prefix.Length) < 0);
            if (project is not null)
            {
                var text = snapshot.Read(project) ?? "";
                if (AppProject().IsMatch(text)) return false;
                return !NotPackable(snapshot, projects, dir, text);
            }
            if (dir.Length == 0) return true;
            dir = System.IO.Path.GetDirectoryName(dir)?.Replace('\\', '/') ?? "";
        }
    }

    /// <summary>IsPackable=false in the project, or in a Directory.Build.props above it that the project doesn't override.</summary>
    static bool NotPackable(IRepoSnapshot snapshot, IReadOnlyList<string> projects, string dir, string projectText)
    {
        if (Packable().Match(projectText) is { Success: true } own) return own.Groups[1].Value.Equals("false", StringComparison.OrdinalIgnoreCase);
        for (var d = dir; ; d = System.IO.Path.GetDirectoryName(d)?.Replace('\\', '/') ?? "")
        {
            var props = (d.Length == 0 ? "" : d + "/") + "Directory.Build.props";
            if (projects.Contains(props) && Packable().Match(snapshot.Read(props) ?? "") is { Success: true } inherited)
                return inherited.Groups[1].Value.Equals("false", StringComparison.OrdinalIgnoreCase);
            if (d.Length == 0) return false;
        }
    }

    [GeneratedRegex(@"<OutputType>\s*(Exe|WinExe|AppContainerExe)\s*</OutputType>|Sdk=""Microsoft\.NET\.Sdk\.(Web|Worker|BlazorWebAssembly)""", RegexOptions.IgnoreCase)]
    private static partial Regex AppProject();

    [GeneratedRegex(@"<IsPackable>\s*(true|false)\s*</IsPackable>", RegexOptions.IgnoreCase)]
    private static partial Regex Packable();
}
