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

    public FileSemanticDiff? SemanticDiff => _semanticDiff.Value;

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

public sealed class PullRequestContext(IReadOnlyList<AnalyzedFile> files)
{
    public IReadOnlyList<AnalyzedFile> Files { get; } = files;

    /// <summary>Hand-written C# files (generated code is excluded).</summary>
    public IEnumerable<AnalyzedFile> CSharpFiles => Files.Where(f => f.IsCSharp && !f.IsGenerated);
}
