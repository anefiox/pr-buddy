using LibGit2Sharp;

namespace PrBuddy.Core.Git;

/// <summary>The repo's C# files as they are in one commit, read straight from git objects (the working tree is never touched).</summary>
public sealed class GitSnapshot : IRepoSnapshot, IDisposable
{
    /// <summary>Bigger files are almost always generated or vendored, and not worth parsing.</summary>
    const long MaxFileBytes = 1_000_000;

    readonly Repository _repo;
    readonly Tree _tree;

    internal GitSnapshot(Repository repo, Commit commit)
    {
        _repo = repo;
        _tree = commit.Tree;
        var paths = new List<string>();
        Collect(_tree, "", paths);
        CSharpPaths = paths;
    }

    public IReadOnlyList<string> CSharpPaths { get; }

    public string? Read(string path) =>
        _tree[path]?.Target is Blob { Size: <= MaxFileBytes, IsBinary: false } blob ? blob.GetContentText() : null;

    static void Collect(Tree tree, string prefix, List<string> paths)
    {
        foreach (var entry in tree)
        {
            var path = prefix + entry.Name;
            if (entry.TargetType == TreeEntryTargetType.Tree) Collect((Tree)entry.Target, path + "/", paths);
            else if (entry.TargetType == TreeEntryTargetType.Blob && FileClassifier.IsCSharp(path)) paths.Add(path);
        }
    }

    public void Dispose() => _repo.Dispose();
}
