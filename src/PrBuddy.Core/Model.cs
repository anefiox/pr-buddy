namespace PrBuddy.Core;

public enum ChangeStatus { Added, Deleted, Modified, Renamed }

public enum Severity { Info, Warning, Critical }

/// <summary>One file in the pull request, with both versions of its content.</summary>
public sealed class ChangedFile
{
    public required string Path { get; init; }

    /// <summary>The path before a rename, otherwise null.</summary>
    public string? OldPath { get; init; }

    public required ChangeStatus Status { get; init; }

    /// <summary>Content at the merge base. Null for added or binary files.</summary>
    public string? OldContent { get; init; }

    /// <summary>Content at the head of the PR. Null for deleted or binary files.</summary>
    public string? NewContent { get; init; }

    /// <summary>1-based line numbers in <see cref="NewContent"/> that the PR added or modified.</summary>
    public IReadOnlySet<int> AddedLines { get; init; } = new HashSet<int>();

    public int DeletedLineCount { get; init; }

    public bool IsBinary { get; init; }
}

/// <summary>Something a rule wants the reviewer to look at. A null <see cref="Path"/> means it is about the whole PR.</summary>
public sealed record Finding(string RuleId, Severity Severity, string Message, string? Path = null, int? Line = null);

/// <summary>A problem with the user's input (bad ref, no repo) rather than a bug.</summary>
public sealed class PrBuddyException(string message) : Exception(message);
