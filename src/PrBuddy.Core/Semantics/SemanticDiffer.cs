using System.Text.Json.Serialization;
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
    /// <summary>Removed from one file and added, unchanged, to another (see <see cref="MemberChange.MovedPath"/>).</summary>
    Moved,
    /// <summary>Only whitespace, comments or line endings changed.</summary>
    CosmeticOnly,
}

/// <param name="Line">Line in the new version, or null for removed members.</param>
/// <param name="OldDisplayName">The previous name, for renames, signature changes and moves.</param>
/// <param name="MovedPath">For a move: the file it came from (in the new file) or went to (in the old one).</param>
/// <param name="Tokens">Added and removed members only: the exact tokens, used to match moves across files.</param>
public sealed record MemberChange(
    string DisplayName,
    string MemberKind,
    MemberChangeKind Change,
    bool IsPublicApi,
    int? Line,
    string? OldDisplayName = null,
    string? MovedPath = null,
    [property: JsonIgnore] string? Tokens = null);

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

        changes.AddRange(unmatchedNew.Select(n => new MemberChange(n.DisplayName, n.Kind, MemberChangeKind.Added, n.IsPublicApi, n.Line, Tokens: n.Tokens)));
        changes.AddRange(unmatchedOld.Select(o => new MemberChange(o.DisplayName, o.Kind, MemberChangeKind.Removed, o.IsPublicApi, null, Tokens: o.Tokens)));

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

    /// <summary>Kinds produced for type declarations (their header: name, modifiers, base list).</summary>
    public static readonly IReadOnlySet<string> TypeKinds = new HashSet<string>(StringComparer.Ordinal) { "class", "struct", "interface", "enum", "record" };

    static void ComparePair(MemberInfo old, MemberInfo current, List<MemberChange> changes)
    {
        // Making a type partial (to split it across files) changes nothing at run time.
        var onlyMadePartial = TypeKinds.Contains(current.Kind) && old.Tokens != current.Tokens
                              && WithoutPartial(old.Tokens) == WithoutPartial(current.Tokens);
        if (old.Tokens == current.Tokens || onlyMadePartial)
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

    static string WithoutPartial(string tokens) =>
        string.Join(" ", tokens.Split(' ').Where(t => t != "partial"));

    static bool HasErrors(SyntaxTree? tree) =>
        tree?.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error) ?? false;
}
