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

    [Fact]
    public void Staged_reads_renames_and_binary_files_without_changing_the_index()
    {
        Repository.Init(_dir);
        Commit("src/Old.cs", "class A { public int M() => 1; }\n", "initial");
        using var repo = new Repository(_dir);
        File.Move(Path.Combine(_dir, "src/Old.cs"), Path.Combine(_dir, "src/New.cs"));
        File.WriteAllBytes(Path.Combine(_dir, "image.png"), [0x89, 0x50, 0x00, 0xFF]);
        Commands.Stage(repo, "*");
        var indexPath = Path.Combine(_dir, ".git/index");
        var originalIndex = File.ReadAllBytes(indexPath);

        var (range, files) = GitDiffReader.ReadStaged(_dir);
        Assert.Equal("HEAD", range.BaseRef);
        Assert.Equal("staged", range.HeadRef);
        var renamed = Assert.Single(files, f => f.Status == ChangeStatus.Renamed);
        Assert.Equal("src/Old.cs", renamed.OldPath);
        Assert.Equal("src/New.cs", renamed.Path);
        Assert.Equal(renamed.OldContent, renamed.NewContent);
        var binary = Assert.Single(files, f => f.IsBinary);
        Assert.Equal("image.png", binary.Path);
        Assert.Null(binary.NewContent);

        using var snapshot = GitDiffReader.OpenIndexSnapshot(_dir);
        Assert.Equal(["src/New.cs"], snapshot.CSharpPaths);
        Assert.Null(snapshot.Read("src/Old.cs"));
        Assert.Equal(renamed.NewContent, snapshot.Read("src/New.cs"));
        Assert.Equal(originalIndex, File.ReadAllBytes(indexPath));
    }

    [Fact]
    public void Staged_rejects_unresolved_conflicts_with_a_friendly_error()
    {
        Repository.Init(_dir);
        Commit("A.cs", "class A { int M() => 1; }\n", "initial");
        using var repo = new Repository(_dir);
        var baseBranch = repo.Head.FriendlyName;
        Commands.Checkout(repo, repo.CreateBranch("feature"));
        Commit("A.cs", "class A { int M() => 2; }\n", "feature");
        Commands.Checkout(repo, repo.Branches[baseBranch]);
        Commit("A.cs", "class A { int M() => 3; }\n", "base");
        repo.Merge(repo.Branches["feature"], _author);
        Assert.True(repo.Index.Conflicts.Any());

        var error = Assert.Throws<PrBuddyException>(() => GitDiffReader.ReadStaged(_dir));
        Assert.Contains("unresolved merge conflicts", error.Message);
        Assert.Throws<PrBuddyException>(() => GitDiffReader.OpenIndexSnapshot(_dir));
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
