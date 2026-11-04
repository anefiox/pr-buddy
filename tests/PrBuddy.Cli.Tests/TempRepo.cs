using LibGit2Sharp;

namespace PrBuddy.Cli.Tests;

/// <summary>A throwaway git repository on disk, starting on an empty "main" branch.</summary>
public sealed class TempRepo : IDisposable
{
    static readonly Signature Author = new("Test", "test@example.com", DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
    readonly Repository _repo;

    public TempRepo(string defaultBranch = "main")
    {
        Dir = Directory.CreateTempSubdirectory("prbuddy-cli-").FullName;
        Repository.Init(Dir);
        // There are no commits yet, so point HEAD at the branch name directly.
        File.WriteAllText(Path.Combine(Dir, ".git", "HEAD"), $"ref: refs/heads/{defaultBranch}\n");
        _repo = new Repository(Dir);
    }

    public string Dir { get; }

    public TempRepo Write(string path, string content)
    {
        var full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return this;
    }

    public TempRepo WriteBytes(string path, byte[] content)
    {
        var full = Full(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return this;
    }

    public TempRepo Delete(string path)
    {
        File.Delete(Full(path));
        return this;
    }

    public TempRepo Move(string from, string to)
    {
        var target = Full(to);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(Full(from), target);
        return this;
    }

    public string Commit(string message = "change")
    {
        Commands.Stage(_repo, "*");
        return _repo.Commit(message, Author, Author, new CommitOptions { AllowEmptyCommit = true }).Sha;
    }

    /// <summary>Adds a file to the index without committing.</summary>
    public TempRepo Stage(string path)
    {
        Commands.Stage(_repo, path);
        return this;
    }

    public TempRepo SetConfig(string key, string value)
    {
        _repo.Config.Set(key, value);
        return this;
    }

    /// <summary>Creates a branch at HEAD and switches to it.</summary>
    public TempRepo Branch(string name)
    {
        Commands.Checkout(_repo, _repo.CreateBranch(name));
        return this;
    }

    public TempRepo Checkout(string name)
    {
        Commands.Checkout(_repo, _repo.Branches[name]);
        return this;
    }

    string Full(string path) => Path.Combine(Dir, path.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose()
    {
        _repo.Dispose();
        // git marks object files read-only, which stops Directory.Delete on Windows.
        foreach (var file in Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(Dir, recursive: true);
    }
}
