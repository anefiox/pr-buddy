using LibGit2Sharp;

namespace PrBuddy.Core.Git;

public sealed record DiffRange(string BaseRef, string HeadRef, string MergeBaseSha, string HeadSha);

/// <summary>Reads what a PR would contain: the diff from the merge base of base and head to head.</summary>
public static class GitDiffReader
{
    static readonly string[] DefaultBaseCandidates = ["origin/main", "origin/master", "main", "master"];

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
                DeletedLineCount = entry.LinesDeleted,
            });
        }

        return (new DiffRange(baseRef, headRef, mergeBase.Sha, headCommit.Sha), files);
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
