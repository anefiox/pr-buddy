using PrBuddy.Core;
using PrBuddy.Core.Rules;

namespace PrBuddy.Tests;

/// <summary>Builds <see cref="ChangedFile"/>s from two strings, so rules can be tested without git.</summary>
internal static class Pr
{
    public static ChangedFile File(string path, string? oldContent, string? newContent)
    {
        var (added, deleted) = LineDiff(oldContent, newContent);
        return new ChangedFile
        {
            Path = path,
            Status = oldContent is null ? ChangeStatus.Added : newContent is null ? ChangeStatus.Deleted : ChangeStatus.Modified,
            OldContent = oldContent,
            NewContent = newContent,
            AddedLines = added,
            DeletedLineCount = deleted,
        };
    }

    public static IReadOnlyList<Finding> Run(IRule rule, params ChangedFile[] files) =>
        rule.Analyze(new PullRequestContext(files.Select(f => new AnalyzedFile(f)).ToList())).ToList();

    /// <summary>Runs a rule with <paramref name="unchanged"/> as the rest of the repo (path → content).</summary>
    public static IReadOnlyList<Finding> RunInRepo(IRule rule, IReadOnlyDictionary<string, string> unchanged, params ChangedFile[] files)
    {
        var all = new Dictionary<string, string>(unchanged);
        foreach (var f in files.Where(f => f.NewContent is not null)) all[f.Path] = f.NewContent!;
        var context = new PullRequestContext(files.Select(f => new AnalyzedFile(f)).ToList(), new InMemorySnapshot(all));
        return rule.Analyze(context).ToList();
    }

    /// <summary>LCS line diff: which new lines (1-based) were added, and how many old lines were deleted.</summary>
    static (HashSet<int> Added, int Deleted) LineDiff(string? oldText, string? newText)
    {
        var a = Lines(oldText);
        var b = Lines(newText);
        var lcs = new int[a.Length + 1, b.Length + 1];
        for (var i = a.Length - 1; i >= 0; i--)
            for (var j = b.Length - 1; j >= 0; j--)
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);

        var added = new HashSet<int>();
        int x = 0, y = 0, deleted = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y]) { x++; y++; }
            else if (lcs[x + 1, y] >= lcs[x, y + 1]) { x++; deleted++; }
            else added.Add(++y);
        }
        while (y < b.Length) added.Add(++y);
        return (added, deleted + (a.Length - x));
    }

    static string[] Lines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var lines = text.Replace("\r\n", "\n").Split('\n');
        return lines[^1] == "" ? lines[..^1] : lines;
    }
}
