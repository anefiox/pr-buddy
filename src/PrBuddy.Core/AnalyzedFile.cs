using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PrBuddy.Core.Semantics;

namespace PrBuddy.Core;

/// <summary>A <see cref="ChangedFile"/> plus lazily computed syntax trees and helpers rules share.</summary>
public sealed class AnalyzedFile
{
    static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    readonly Lazy<SyntaxTree?> _oldTree;
    readonly Lazy<SyntaxTree?> _newTree;
    readonly Lazy<FileSemanticDiff?> _semanticDiff;
    string[]? _newLines;

    public AnalyzedFile(ChangedFile file)
    {
        File = file;
        IsCSharp = !file.IsBinary && FileClassifier.IsCSharp(file.Path);
        IsTestFile = FileClassifier.IsTestFile(file.Path);
        IsGenerated = IsCSharp && FileClassifier.IsGenerated(file.Path, file.NewContent ?? file.OldContent);
        Kind = IsGenerated ? FileKind.Generated
            : IsCSharp ? (IsTestFile ? FileKind.Test : FileKind.Code)
            : FileClassifier.IsBuildConfig(file.Path) || FileClassifier.IsProjectFile(file.Path) ? FileKind.BuildConfig
            : FileKind.Other;

        _oldTree = new(() => Parse(file.OldContent, file.OldPath ?? file.Path));
        _newTree = new(() => Parse(file.NewContent, file.Path));
        _semanticDiff = new(() => IsCSharp && !IsGenerated ? SemanticDiffer.Compare(file.Path, OldTree, NewTree) : null);
    }

    public ChangedFile File { get; }
    public string Path => File.Path;
    public bool IsCSharp { get; }
    public bool IsTestFile { get; }
    public bool IsGenerated { get; }
    public FileKind Kind { get; }

    /// <summary>Syntax tree of the merge-base version, or null if not C# or the file is new.</summary>
    public SyntaxTree? OldTree => _oldTree.Value;

    /// <summary>Syntax tree of the PR version, or null if not C# or the file was deleted.</summary>
    public SyntaxTree? NewTree => _newTree.Value;

    public FileSemanticDiff? SemanticDiff => _replacedDiff ?? _semanticDiff.Value;

    FileSemanticDiff? _replacedDiff;

    /// <summary>For cross-file analysis (moves) that can only run once every file has been diffed.</summary>
    internal void ReplaceSemanticDiff(FileSemanticDiff diff) => _replacedDiff = diff;

    public bool IsAddedLine(int line) => File.AddedLines.Contains(line);
    public bool IsNew(SyntaxNode node) => IsAddedLine(LineOf(node));
    public bool IsNew(SyntaxToken token) => IsAddedLine(LineOf(token));
    public bool IsNew(SyntaxTrivia trivia) => IsAddedLine(LineOf(trivia));

    /// <summary>True if any line the node spans was added or modified by the PR.</summary>
    public bool TouchesAddedLines(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        for (var line = span.StartLinePosition.Line + 1; line <= span.EndLinePosition.Line + 1; line++)
            if (IsAddedLine(line)) return true;
        return false;
    }

    /// <summary>The added lines with their text, for non-C# rules that work line by line.</summary>
    public IEnumerable<(int Line, string Text)> AddedLineTexts()
    {
        _newLines ??= (File.NewContent ?? "").Split('\n');
        foreach (var line in File.AddedLines.Order())
            if (line - 1 < _newLines.Length)
                yield return (line, _newLines[line - 1].TrimEnd('\r'));
    }

    public static int LineOf(SyntaxNode node) => node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    public static int LineOf(SyntaxToken token) => token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
    public static int LineOf(SyntaxTrivia trivia) => trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    SyntaxTree? Parse(string? content, string path) =>
        IsCSharp && content is not null ? CSharpSyntaxTree.ParseText(content, ParseOptions, path) : null;
}

public sealed class PullRequestContext
{
    readonly Func<string, bool> _isIgnored;
    List<(string Path, SyntaxTree Tree)>? _repoTrees;

    /// <param name="snapshot">The whole repo at the PR head. Defaults to just the PR's own files.</param>
    /// <param name="isIgnored">Paths to leave out of repo-wide searches (from prbuddy.json).</param>
    /// <param name="history">Loads recent commit history on first use; omitted means no history.</param>
    public PullRequestContext(
        IReadOnlyList<AnalyzedFile> files,
        IRepoSnapshot? snapshot = null,
        Func<string, bool>? isIgnored = null,
        Func<ChangeHistory>? history = null)
    {
        Files = files;
        Snapshot = snapshot ?? InMemorySnapshot.FromChangedFiles(files.Select(f => f.File));
        _isIgnored = isIgnored ?? (_ => false);
        _history = new Lazy<ChangeHistory>(history ?? (() => ChangeHistory.Empty));
    }

    readonly Lazy<ChangeHistory> _history;

    /// <summary>Recent commits before the PR. Loaded on first use, because only some rules need it.</summary>
    public ChangeHistory History => _history.Value;

    public IReadOnlyList<AnalyzedFile> Files { get; }

    /// <summary>Hand-written C# files (generated code is excluded).</summary>
    public IEnumerable<AnalyzedFile> CSharpFiles => Files.Where(f => f.IsCSharp && !f.IsGenerated);

    public IRepoSnapshot Snapshot { get; }

    /// <summary>
    /// Every hand-written C# file in the repo at the PR head, parsed once and shared between rules.
    /// Files the PR changed reuse their already-parsed tree.
    /// </summary>
    public IReadOnlyList<(string Path, SyntaxTree Tree)> RepoTrees()
    {
        if (_repoTrees is not null) return _repoTrees;

        var changed = Files.Where(f => f.IsCSharp && f.NewTree is not null).ToDictionary(f => f.Path, f => f);
        var parseOptions = new CSharpParseOptions(LanguageVersion.Preview);

        // Read in order on this thread (the snapshot may sit on a git repository, which isn't
        // thread-safe), then parse in parallel (Roslyn parsing is). AsOrdered keeps the output
        // order, and with it every rule's results, the same from run to run.
        var work = new List<(string Path, AnalyzedFile? Changed, string? Content)>();
        foreach (var path in Snapshot.CSharpPaths)
        {
            if (_isIgnored(path)) continue;
            if (changed.TryGetValue(path, out var file))
            {
                if (!file.IsGenerated) work.Add((path, file, null));
                continue;
            }
            var content = Snapshot.Read(path);
            if (content is not null && !FileClassifier.IsGenerated(path, content)) work.Add((path, null, content));
        }

        _repoTrees = work
            .AsParallel().AsOrdered()
            .Select(w => (w.Path, w.Changed?.NewTree ?? CSharpSyntaxTree.ParseText(w.Content!, parseOptions, w.Path)))
            .ToList();
        return _repoTrees;
    }
}
