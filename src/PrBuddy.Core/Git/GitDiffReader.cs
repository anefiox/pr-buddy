using LibGit2Sharp;

namespace PrBuddy.Core.Git;

public sealed record DiffRange(string BaseRef, string HeadRef, string MergeBaseSha, string HeadSha);

/// <summary>Reads what a PR would contain: the diff from the merge base of base and head to head.</summary>
public static class GitDiffReader
{
    static readonly string[] DefaultBaseCandidates = ["origin/main", "origin/master", "main", "master"];

    /// <summary>The root folder of the working tree containing <paramref name="repoPath"/>.</summary>
    public static string WorkingDirectory(string repoPath)
    {
        using var repo = Open(repoPath);
        return repo.Info.WorkingDirectory ?? throw new PrBuddyException("This is a bare repository; there's no working tree.");
    }

    /// <summary>The repo's C# files at a commit. Dispose it when done; it keeps the repository open.</summary>
    public static GitSnapshot OpenSnapshot(string repoPath, string commitSha)
    {
        var repo = Open(repoPath);
        try
        {
            var commit = repo.Lookup<Commit>(commitSha) ?? throw new PrBuddyException($"Couldn't find commit {commitSha}.");
            return new GitSnapshot(repo, commit);
        }
        catch
        {
            repo.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The C# paths each of the last <paramref name="maxCommits"/> non-merge commits changed, walking
    /// back from <paramref name="fromSha"/> (the merge base, so the PR's own commits don't count).
    /// </summary>
    public static ChangeHistory ReadHistory(string repoPath, string fromSha, int maxCommits = 300)
    {
        using var repo = Open(repoPath);
        var start = repo.Lookup<Commit>(fromSha);
        if (start is null) return ChangeHistory.Empty;

        var filter = new CommitFilter { IncludeReachableFrom = start, SortBy = CommitSortStrategies.Topological };
        var commits = new List<IReadOnlySet<string>>();
        foreach (var commit in repo.Commits.QueryBy(filter))
        {
            if (commits.Count >= maxCommits) break;
            var parents = commit.Parents.ToList();
            if (parents.Count != 1) continue; // skip merges and the root commit
            using var changes = repo.Diff.Compare<TreeChanges>(parents[0].Tree, commit.Tree);
            var paths = changes.Select(c => c.Path).Where(FileClassifier.IsCSharp).ToHashSet(StringComparer.Ordinal);
            if (paths.Count > 0) commits.Add(paths);
        }
        return new ChangeHistory(commits);
    }

    /// <summary>A file's content at a given commit, or null if it doesn't exist there.</summary>
    public static string? ReadFile(string repoPath, string commitSha, string path)
    {
        using var repo = Open(repoPath);
        return repo.Lookup<Commit>(commitSha)?[path]?.Target is Blob blob ? blob.GetContentText() : null;
    }

    public static (DiffRange Range, IReadOnlyList<ChangedFile> Files) Read(string repoPath, string? baseRef = null, string headRef = "HEAD")
    {
        using var repo = Open(repoPath);

        baseRef ??= DefaultBaseCandidates.FirstOrDefault(b => repo.Branches[b] is not null)
            ?? throw new PrBuddyException("Couldn't find a main or master branch. Pass the base branch explicitly.");

        var baseCommit = Resolve(repo, baseRef);
        var headCommit = Resolve(repo, headRef);
        // Like GitHub: only what head adds on top of where it branched off, not what base gained since.
        var mergeBase = repo.ObjectDatabase.FindMergeBase(baseCommit, headCommit) ?? baseCommit;

        var options = new CompareOptions { ContextLines = 0, Similarity = SimilarityOptions.Renames };
        using var patch = repo.Diff.Compare<Patch>(mergeBase.Tree, headCommit.Tree, options);

        var files = new List<ChangedFile>();
        foreach (var entry in patch)
        {
            if (Map(entry.Status) is not { } status) continue;
            var binary = entry.IsBinaryComparison;
            files.Add(new ChangedFile
            {
                Path = entry.Path,
                OldPath = entry.OldPath != entry.Path ? entry.OldPath : null,
                Status = status,
                IsBinary = binary,
                OldContent = binary || status == ChangeStatus.Added ? null : Content(repo, entry.OldOid),
                NewContent = binary || status == ChangeStatus.Deleted ? null : Content(repo, entry.Oid),
                AddedLines = entry.AddedLines.Select(l => l.LineNumber).ToHashSet(),
                DeletedLines = entry.DeletedLines.Select(l => l.LineNumber).ToHashSet(),
                DeletedLineCount = entry.LinesDeleted,
            });
        }

        return (new DiffRange(baseRef, headRef, mergeBase.Sha, headCommit.Sha), files);
    }

    /// <summary>Only the staged changes against HEAD, including the staged version of partially staged files.</summary>
    public static (DiffRange Range, IReadOnlyList<ChangedFile> Files) ReadStaged(string repoPath)
    {
        using var repo = Open(repoPath);
        var head = repo.Head.Tip ?? throw new PrBuddyException("The repository has no commits yet, so there's nothing to compare against.");
        EnsureResolvedIndex(repo);
        var options = new CompareOptions { ContextLines = 0, Similarity = SimilarityOptions.Renames };
        using var patch = repo.Diff.Compare<Patch>(head.Tree, DiffTargets.Index, null, null, options);
        var files = new List<ChangedFile>();
        foreach (var entry in patch)
        {
            if (Map(entry.Status) is not { } status) continue;
            var binary = entry.IsBinaryComparison;
            files.Add(new ChangedFile
            {
                Path = entry.Path,
                OldPath = entry.OldPath != entry.Path ? entry.OldPath : null,
                Status = status,
                IsBinary = binary,
                OldContent = binary || status == ChangeStatus.Added ? null : Content(repo, entry.OldOid),
                NewContent = binary || status == ChangeStatus.Deleted ? null : Content(repo, entry.Oid),
                AddedLines = entry.AddedLines.Select(l => l.LineNumber).ToHashSet(),
                DeletedLines = entry.DeletedLines.Select(l => l.LineNumber).ToHashSet(),
                DeletedLineCount = entry.LinesDeleted,
            });
        }
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return (new DiffRange("HEAD", "staged", head.Sha, head.Sha), files);
    }

    /// <summary>The repo's C# files in the index, including staged additions and deletions.</summary>
    public static GitSnapshot OpenIndexSnapshot(string repoPath)
    {
        var repo = Open(repoPath);
        try
        {
            EnsureResolvedIndex(repo);
            return new GitSnapshot(repo);
        }
        catch
        {
            repo.Dispose();
            throw;
        }
    }

    static void EnsureResolvedIndex(Repository repo)
    {
        if (repo.Index.Conflicts.Any())
            throw new PrBuddyException("The index has unresolved merge conflicts. Resolve and stage them before using --staged.");
    }

    /// <summary>
    /// What a PR would contain if you committed everything right now: merge base → working tree,
    /// including staged, unstaged and untracked (but not ignored) files.
    /// </summary>
    public static (DiffRange Range, IReadOnlyList<ChangedFile> Files) ReadUncommitted(string repoPath, string? baseRef = null)
    {
        using var repo = Open(repoPath);
        var workdir = repo.Info.WorkingDirectory ?? throw new PrBuddyException("This is a bare repository; there's no working tree to check.");
        var headCommit = repo.Head.Tip ?? throw new PrBuddyException("The repository has no commits yet, so there's nothing to compare against.");

        baseRef ??= DefaultBaseCandidates.FirstOrDefault(b => repo.Branches[b] is not null)
            ?? throw new PrBuddyException("Couldn't find a main or master branch. Pass the base branch explicitly.");
        var baseCommit = Resolve(repo, baseRef);
        var mergeBase = repo.ObjectDatabase.FindMergeBase(baseCommit, headCommit) ?? baseCommit;

        var options = new CompareOptions { ContextLines = 0, Similarity = SimilarityOptions.Renames };
        using var patch = repo.Diff.Compare<Patch>(mergeBase.Tree, DiffTargets.Index | DiffTargets.WorkingDirectory, null, null, options);

        var files = new List<ChangedFile>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in patch)
        {
            if (Map(entry.Status) is not { } status) continue;
            seen.Add(entry.Path);
            var binary = entry.IsBinaryComparison;
            var oldContent = binary || status == ChangeStatus.Added ? null : Content(repo, entry.OldOid);
            files.Add(new ChangedFile
            {
                Path = entry.Path,
                OldPath = entry.OldPath != entry.Path ? entry.OldPath : null,
                Status = status,
                IsBinary = binary,
                OldContent = oldContent,
                NewContent = binary || status == ChangeStatus.Deleted ? null : ReadWorkingFile(workdir, entry.Path, oldContent),
                AddedLines = entry.AddedLines.Select(l => l.LineNumber).ToHashSet(),
                DeletedLineCount = entry.LinesDeleted,
            });
        }

        // The tree-to-workdir diff leaves out files git doesn't know about yet.
        var workingStatus = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true, RecurseUntrackedDirs = true, IncludeIgnored = false });
        foreach (var item in workingStatus.Untracked)
        {
            if (!seen.Add(item.FilePath)) continue;
            var full = Path.Combine(workdir, item.FilePath);
            var binary = WorkingTreeSnapshot.LooksBinary(full);
            var content = binary ? null : File.ReadAllText(full);
            files.Add(new ChangedFile
            {
                Path = item.FilePath,
                Status = ChangeStatus.Added,
                IsBinary = binary,
                NewContent = content,
                AddedLines = Enumerable.Range(1, content is null ? 0 : LineCount(content)).ToHashSet(),
            });
        }

        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return (new DiffRange(baseRef, "working tree", mergeBase.Sha, headCommit.Sha), files);
    }

    /// <summary>The repo's C# files as they are on disk right now. Doesn't keep anything open.</summary>
    public static WorkingTreeSnapshot OpenWorkingTreeSnapshot(string repoPath)
    {
        using var repo = Open(repoPath);
        var workdir = repo.Info.WorkingDirectory ?? throw new PrBuddyException("This is a bare repository; there's no working tree to check.");
        var tracked = repo.Index.Select(e => e.Path);
        var untracked = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true, RecurseUntrackedDirs = true, IncludeIgnored = false })
            .Untracked.Select(e => e.FilePath);
        var paths = tracked.Concat(untracked)
            .Select(p => p.Replace('\\', '/'))
            .Where(FileClassifier.IsCSharp)
            .Where(p => File.Exists(Path.Combine(workdir, p)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        return new WorkingTreeSnapshot(workdir, paths);
    }

    /// <summary>
    /// Reads a file from disk. If git stores it with LF but checks it out with CRLF (core.autocrlf),
    /// undo that so every member doesn't look reformatted.
    /// </summary>
    static string ReadWorkingFile(string workdir, string path, string? oldContent)
    {
        var text = File.ReadAllText(Path.Combine(workdir, path));
        return oldContent is not null && !oldContent.Contains('\r') ? text.Replace("\r\n", "\n") : text;
    }

    static int LineCount(string text)
    {
        if (text.Length == 0) return 0;
        var count = text.Count(c => c == '\n');
        return text.EndsWith('\n') ? count : count + 1;
    }

    static Repository Open(string repoPath)
    {
        var gitDir = Repository.Discover(Path.GetFullPath(repoPath))
            ?? throw new PrBuddyException($"No git repository found at '{repoPath}'.");
        return new Repository(gitDir);
    }

    static Commit Resolve(Repository repo, string rev)
    {
        try
        {
            return repo.Lookup<Commit>(rev) ?? throw new PrBuddyException($"Couldn't resolve '{rev}' to a commit.");
        }
        catch (LibGit2SharpException)
        {
            throw new PrBuddyException($"Couldn't resolve '{rev}' to a commit.");
        }
    }

    static ChangeStatus? Map(ChangeKind kind) => kind switch
    {
        ChangeKind.Added or ChangeKind.Copied => ChangeStatus.Added,
        ChangeKind.Deleted => ChangeStatus.Deleted,
        ChangeKind.Renamed => ChangeStatus.Renamed,
        ChangeKind.Modified or ChangeKind.TypeChanged or ChangeKind.Conflicted => ChangeStatus.Modified,
        _ => null,
    };

    static string? Content(Repository repo, ObjectId id) => repo.Lookup<Blob>(id)?.GetContentText();
}
