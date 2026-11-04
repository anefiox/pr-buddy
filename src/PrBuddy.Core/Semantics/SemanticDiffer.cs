using Microsoft.CodeAnalysis;

namespace PrBuddy.Core.Semantics;

public enum MemberChangeKind
{
    /// <summary>Same signature, different code inside.</summary>
    BehaviorChanged,
    /// <summary>Attributes, modifiers, return type or parameters changed.</summary>
    SignatureChanged,
    Added,
    Removed,
    /// <summary>Identical body under a new name or in another type.</summary>
    Renamed,
    /// <summary>Only whitespace, comments or line endings changed.</summary>
    CosmeticOnly,
}

/// <param name="Line">Line in the new version, or null for removed members.</param>
/// <param name="OldDisplayName">The previous name, for renames and signature changes.</param>
public sealed record MemberChange(
    string DisplayName,
    string MemberKind,
    MemberChangeKind Change,
    bool IsPublicApi,
    int? Line,
    string? OldDisplayName = null);

public sealed record FileSemanticDiff(string Path, IReadOnlyList<MemberChange> Changes, bool HasParseErrors)
{
    /// <summary>True when nothing changed meaning: only formatting, comments or member order.</summary>
    public bool IsCosmeticOnly => Changes.All(c => c.Change == MemberChangeKind.CosmeticOnly);

    public int RealChangeCount => Changes.Count(c => c.Change != MemberChangeKind.CosmeticOnly);
}

/// <summary>
/// Compares two versions of a C# file member by member, ignoring trivia (whitespace, comments),
/// so a reviewer can tell the handful of real changes apart from reformatting noise.
/// Members that only moved within the file are not reported at all.
/// </summary>
public static class SemanticDiffer
{
    public static FileSemanticDiff Compare(string path, SyntaxTree? oldTree, SyntaxTree? newTree)
    {
        List<MemberInfo> oldMembers = oldTree is null ? [] : MemberExtractor.Extract(oldTree.GetRoot());
        List<MemberInfo> newMembers = newTree is null ? [] : MemberExtractor.Extract(newTree.GetRoot());
        var changes = new List<MemberChange>();

        // Pass 1: same key in both versions.
        var newByKey = newMembers.GroupBy(m => m.Key).ToDictionary(g => g.Key, g => new Queue<MemberInfo>(g));
        var unmatchedOld = new List<MemberInfo>();
        foreach (var old in oldMembers)
        {
            if (newByKey.TryGetValue(old.Key, out var queue) && queue.TryDequeue(out var current))
                ComparePair(old, current, changes);
            else
                unmatchedOld.Add(old);
        }
        var unmatchedNew = newByKey.Values.SelectMany(q => q).OrderBy(m => m.Line).ToList();

        // Pass 2: same name, different parameters, and no other overload to confuse it with.
        foreach (var old in unmatchedOld.ToList())
        {
            var candidates = unmatchedNew.Where(n => n.MatchName == old.MatchName).ToList();
            if (candidates.Count == 1 && unmatchedOld.Count(o => o.MatchName == old.MatchName) == 1)
                Pair(old, candidates[0], MemberChangeKind.SignatureChanged);
        }

        // Pass 3: same body under another name or in another type.
        foreach (var old in unmatchedOld.ToList())
        {
            if (old.BodyTokens.Length == 0) continue;
            var twin = unmatchedNew.FirstOrDefault(n => n.Kind == old.Kind && n.BodyTokens == old.BodyTokens);
            if (twin is not null) Pair(old, twin, MemberChangeKind.Renamed);
        }

        changes.AddRange(unmatchedNew.Select(n => new MemberChange(n.DisplayName, n.Kind, MemberChangeKind.Added, n.IsPublicApi, n.Line)));
        changes.AddRange(unmatchedOld.Select(o => new MemberChange(o.DisplayName, o.Kind, MemberChangeKind.Removed, o.IsPublicApi, null)));

        var ordered = changes.OrderBy(c => c.Line ?? int.MaxValue).ToList();
        return new FileSemanticDiff(path, ordered, HasErrors(oldTree) || HasErrors(newTree));

        void Pair(MemberInfo old, MemberInfo current, MemberChangeKind kind)
        {
            changes.Add(new MemberChange(current.DisplayName, current.Kind, kind,
                old.IsPublicApi || current.IsPublicApi, current.Line, old.DisplayName));
            unmatchedOld.Remove(old);
            unmatchedNew.Remove(current);
        }
    }

    static void ComparePair(MemberInfo old, MemberInfo current, List<MemberChange> changes)
    {
        if (old.Tokens == current.Tokens)
        {
            if (old.RawText != current.RawText)
                changes.Add(new MemberChange(current.DisplayName, current.Kind, MemberChangeKind.CosmeticOnly, current.IsPublicApi, current.Line));
            return;
        }

        var kind = old.SignatureTokens == current.SignatureTokens
            ? MemberChangeKind.BehaviorChanged
            : MemberChangeKind.SignatureChanged;
        changes.Add(new MemberChange(current.DisplayName, current.Kind, kind, old.IsPublicApi || current.IsPublicApi, current.Line));
    }

    static bool HasErrors(SyntaxTree? tree) =>
        tree?.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error) ?? false;
}
