using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB035: a new #nullable enable at the top of one file. In a project with nullable off it turns
/// on warnings for just that file, which pulls unrelated annotation churn into the PR; in a project
/// with nullable on it does nothing.
/// </summary>
public sealed partial class FileNullableEnableRule : RuleBase
{
    public override RuleDescriptor Descriptor { get; } = new("PRB035", "Nullable turned on for one file", Severity.Info, "Suppressions");

    public override IEnumerable<Finding> Analyze(PullRequestContext context)
    {
        foreach (var file in context.CSharpFiles.Where(f => f.NewTree is not null && !f.IsGenerated))
        {
            var directive = file.NewTree!.GetRoot().DescendantTrivia(descendIntoTrivia: true)
                .Select(t => t.GetStructure()).OfType<NullableDirectiveTriviaSyntax>()
                .FirstOrDefault(d => d.SettingToken.IsKind(SyntaxKind.EnableKeyword) && d.TargetToken.IsKind(SyntaxKind.None) && file.IsNew(d));
            if (directive is null) continue;

            // A project that already enables nullable file by file is migrating on purpose.
            if (IsProjectConvention(context, file.Path)) continue;

            var projectSetting = NullableSetting(context.Snapshot, file.Path);
            var message = projectSetting switch
            {
                true => "#nullable enable is new here, but the project already turns nullable on, so it does nothing. Remove it.",
                false => "#nullable enable turns nullable warnings on for this one file while the project has them off. " +
                         "That pulls annotation changes into code the PR isn't about. Leave it off, or turn it on for the whole project in its own PR.",
                null => "#nullable enable turns nullable warnings on for this one file. If the project has them off, this pulls annotation " +
                        "changes into code the PR isn't about; if it has them on, the line does nothing.",
            };
            yield return Report(file, directive, message);
        }
    }

    /// <summary>The project's &lt;Nullable&gt; setting from the nearest .csproj or Directory.Build.props; null if no project is found.</summary>
    static bool? NullableSetting(IRepoSnapshot snapshot, string path)
    {
        var projects = snapshot.ProjectPaths;
        if (projects.Count == 0) return null;
        var foundProject = false;
        for (var dir = Parent(path); ; dir = Parent(dir))
        {
            var prefix = dir.Length == 0 ? "" : dir + "/";
            foreach (var candidate in projects.Where(p => p.StartsWith(prefix, StringComparison.Ordinal) && p.IndexOf('/', prefix.Length) < 0)
                         .OrderBy(p => p.EndsWith(".props", StringComparison.OrdinalIgnoreCase)))
            {
                foundProject |= candidate.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase);
                if (Nullable().Match(snapshot.Read(candidate) ?? "") is { Success: true } setting)
                    return setting.Groups[1].Value.Equals("enable", StringComparison.OrdinalIgnoreCase);
            }
            if (dir.Length == 0) return foundProject ? false : null;
        }
    }

    /// <summary>At least this many other files in the same project already start with #nullable enable.</summary>
    const int ConventionThreshold = 10;

    static bool IsProjectConvention(PullRequestContext context, string path)
    {
        var projects = context.Snapshot.ProjectPaths.Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToArray();
        string? root = null;
        for (var dir = Parent(path); root is null; dir = Parent(dir))
        {
            var prefix = dir.Length == 0 ? "" : dir + "/";
            if (projects.Any(p => p.StartsWith(prefix, StringComparison.Ordinal) && p.IndexOf('/', prefix.Length) < 0)) root = prefix;
            else if (dir.Length == 0) return false;
        }
        var changed = context.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var count = 0;
        foreach (var other in context.Snapshot.CSharpPaths.Where(p => p.StartsWith(root, StringComparison.Ordinal) && !changed.Contains(p)).Take(3000))
            if ((context.Snapshot.Read(other) ?? "").Contains("#nullable enable", StringComparison.Ordinal) && ++count >= ConventionThreshold)
                return true;
        return false;
    }

    static string Parent(string path) => System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";

    [GeneratedRegex(@"<Nullable>\s*(enable|disable|warnings|annotations)\s*</Nullable>", RegexOptions.IgnoreCase)]
    private static partial Regex Nullable();
}
