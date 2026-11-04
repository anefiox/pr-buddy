using LibGit2Sharp;

namespace PrBuddy.Core.Git;

/// <summary>The repo's C# files as they are in one commit, read straight from git objects (the working tree is never touched).</summary>
public sealed class GitSnapshot : IRepoSnapshot, IDisposable
{
    /// <summary>Bigger files are almost always generated or vendored, and not worth parsing.</summary>
    const long MaxFileBytes = 1_000_000;

    readonly Repository _repo;
    // Remembered from the one walk of the tree, so reading a file never looks its path up again.
    readonly Dictionary<string, Blob> _blobs = new(StringComparer.Ordinal);

    internal GitSnapshot(Repository repo, Commit commit)
    {
        _repo = repo;
        var paths = new List<string>();
        Collect(commit.Tree, "", paths);
        CSharpPaths = paths;
    }

    public IReadOnlyList<string> CSharpPaths { get; }

    public string? Read(string path) =>
        _blobs.TryGetValue(path, out var blob) && blob is { Size: <= MaxFileBytes, IsBinary: false } ? blob.GetContentText() : null;

    void Collect(Tree tree, string prefix, List<string> paths)
    {
        foreach (var entry in tree)
        {
            var path = prefix + entry.Name;
            if (entry.TargetType == TreeEntryTargetType.Tree) Collect((Tree)entry.Target, path + "/", paths);
            else if (entry.TargetType == TreeEntryTargetType.Blob && FileClassifier.IsCSharp(path))
            {
                paths.Add(path);
                _blobs[path] = (Blob)entry.Target;
            }
        }
    }

    public void Dispose() => _repo.Dispose();
}

/// <summary>The repo's C# files as they are on disk, for --uncommitted.</summary>
public sealed class WorkingTreeSnapshot : IRepoSnapshot, IDisposable
{
    const long MaxFileBytes = 1_000_000;
    readonly string _workdir;

    internal WorkingTreeSnapshot(string workdir, IReadOnlyList<string> paths)
    {
        _workdir = workdir;
        CSharpPaths = paths;
    }

    public IReadOnlyList<string> CSharpPaths { get; }

    public string? Read(string path)
    {
        var full = Path.Combine(_workdir, path);
        var info = new FileInfo(full);
        return info.Exists && info.Length <= MaxFileBytes && !LooksBinary(full) ? File.ReadAllText(full) : null;
    }

    /// <summary>Same test git uses: a NUL byte in the first 8000 bytes.</summary>
    internal static bool LooksBinary(string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[8000];
        var read = stream.Read(buffer, 0, buffer.Length);
        return Array.IndexOf(buffer, (byte)0, 0, read) >= 0;
    }

    public void Dispose() { }
}
