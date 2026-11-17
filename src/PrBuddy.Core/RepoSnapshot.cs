namespace PrBuddy.Core;

/// <summary>
/// Read-only view of the repository's C# files at the PR's head, for rules that need to look beyond
/// the diff (is this used anywhere? does this already exist?).
/// </summary>
public interface IRepoSnapshot
{
    /// <summary>Paths of every C# file, relative to the repository root, with forward slashes.</summary>
    IReadOnlyList<string> CSharpPaths { get; }

    /// <summary>File content, or null if it doesn't exist or is too big to be worth reading.</summary>
    string? Read(string path);

    /// <summary>Paths of project files (*.csproj) and Directory.Build.props, which <see cref="Read"/> can read.</summary>
    IReadOnlyList<string> ProjectPaths => [];
}

public sealed class InMemorySnapshot(IReadOnlyDictionary<string, string> files) : IRepoSnapshot
{
    public IReadOnlyList<string> CSharpPaths { get; } =
        files.Keys.Where(FileClassifier.IsCSharp).Order(StringComparer.Ordinal).ToList();

    public string? Read(string path) => files.TryGetValue(path, out var content) ? content : null;

    public IReadOnlyList<string> ProjectPaths { get; } =
        files.Keys.Where(FileClassifier.IsProjectOrBuildProps).Order(StringComparer.Ordinal).ToList();

    /// <summary>A snapshot of just the PR's own files, for when the rest of the repo isn't available.</summary>
    public static InMemorySnapshot FromChangedFiles(IEnumerable<ChangedFile> files) =>
        new(files.Where(f => f.NewContent is not null).ToDictionary(f => f.Path, f => f.NewContent!));
}
