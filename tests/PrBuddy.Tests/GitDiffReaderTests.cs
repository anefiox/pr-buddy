using LibGit2Sharp;
using PrBuddy.Core;
using PrBuddy.Core.Git;

namespace PrBuddy.Tests;

public sealed class GitDiffReaderTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("prbuddy-").FullName;
    readonly Signature _author = new("Test", "test@example.com", DateTimeOffset.Now);

    [Fact]
    public void Reads_only_what_the_branch_added_since_the_merge_base()
    {
        Repository.Init(_dir);
        using var repo = new Repository(_dir);

        Commit("src/Calc.cs", "class Calc\n{\n    int A() => 1;\n}\n", "initial");
        var baseBranch = repo.Head.FriendlyName;

        Commands.Checkout(repo, repo.CreateBranch("feature"));
        Commit("src/Calc.cs", "class Calc\n{\n    int A() => 1;\n    int B() => 2;\n}\n", "add B");

        // The base moves on after the branch was cut. That must not show up in the PR diff.
        Commands.Checkout(repo, repo.Branches[baseBranch]);
        Commit("src/Other.cs", "class Other { }\n", "unrelated");

        var (range, files) = GitDiffReader.Read(_dir, baseBranch, "feature");

        var file = Assert.Single(files);
        Assert.Equal("src/Calc.cs", file.Path);
        Assert.Equal(ChangeStatus.Modified, file.Status);
        Assert.Equal([4], file.AddedLines);
        Assert.Contains("int B()", file.NewContent);
        Assert.DoesNotContain("int B()", file.OldContent);
        Assert.Equal(repo.Branches["feature"].Tip.Sha, range.HeadSha);
    }

    [Fact]
    public void Unknown_ref_is_a_friendly_error()
    {
        Repository.Init(_dir);
        Commit("a.txt", "a", "initial");

        var ex = Assert.Throws<PrBuddyException>(() => GitDiffReader.Read(_dir, "does-not-exist"));
        Assert.Contains("does-not-exist", ex.Message);
    }

    void Commit(string path, string content, string message)
    {
        using var repo = new Repository(_dir);
        var full = Path.Combine(_dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        Commands.Stage(repo, path);
        repo.Commit(message, _author, _author);
    }

    public void Dispose()
    {
        // git marks object files read-only, which stops Directory.Delete on Windows.
        foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_dir, recursive: true);
    }
}
