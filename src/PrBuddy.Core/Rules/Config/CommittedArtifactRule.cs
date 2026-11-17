namespace PrBuddy.Core.Rules.Config;

/// <summary>
/// PRB205: a file the PR adds that is a local database, a build output or an IDE's per-user state.
/// These are machine-specific, often large, can hold real data, and get overwritten on the next build.
/// </summary>
public sealed class CommittedArtifactRule : RuleBase
{
    public override RuleDescriptor Descriptor { get; } = new("PRB205", "Local or build artifact committed", Severity.Warning, "Config");

    static readonly (string[] Endings, string What)[] Kinds =
    [
        ([".sqlite", ".sqlite3", ".db", ".db-shm", ".db-wal", ".mdf", ".ldf", ".bak"], "a database file, which can hold real data and changes every time the app runs"),
        ([".pdb", ".nupkg", ".snupkg"], "a build output"),
        ([".suo", ".user", ".userprefs", ".DS_Store", "Thumbs.db"], "per-user IDE or OS state"),
        ([".log", ".trx", ".coverage"], "a log or test result"),
    ];

    static readonly string[] OutputFolders = ["/bin/", "/obj/", "/.vs/", "/TestResults/"];

    public override IEnumerable<Finding> Analyze(PullRequestContext context)
    {
        foreach (var file in context.Files.Where(f => f.File.Status == ChangeStatus.Added))
        {
            var path = "/" + file.Path.Replace('\\', '/');
            var folder = OutputFolders.FirstOrDefault(f => path.Contains(f, StringComparison.OrdinalIgnoreCase));
            var kind = Kinds.FirstOrDefault(k => k.Endings.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase)));
            if (folder is null && kind.What is null) continue;
            // Test fixtures are often real databases and logs on purpose.
            if (folder is null && file.IsTestFile || folder is null && IsUnderTestData(path)) continue;

            var what = folder is not null ? $"inside a {folder.Trim('/')} folder, so it's build or IDE output" : kind.What;
            yield return Report(file.Path, null,
                $"'{file.Path}' looks like {what}. It probably shouldn't be committed: remove it and add the pattern to .gitignore, " +
                "or say in the PR why it belongs in the repo.");
        }
    }

    static bool IsUnderTestData(string path) =>
        path.Split('/').Any(segment => segment.Equals("testdata", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("fixtures", StringComparison.OrdinalIgnoreCase) || segment.Equals("samples", StringComparison.OrdinalIgnoreCase));
}
