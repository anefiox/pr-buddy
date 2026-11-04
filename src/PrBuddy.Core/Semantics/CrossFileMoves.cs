namespace PrBuddy.Core.Semantics;

/// <summary>
/// Finds members the PR moved between files: removed from one file and added, token for token, to
/// another (formatting and comments may differ). Both sides become <see cref="MemberChangeKind.Moved"/>,
/// so a "split this file up" refactor reads as moves, not as hundreds of lines of new code, and the
/// rules for new code (unused, duplicated, complex, untested) leave it alone.
/// </summary>
public static class CrossFileMoves
{
    /// <summary>Characters of token text below which a member is too common to call a move with confidence (`int Id { get; set; }` is ~27).</summary>
    const int MinTokenChars = 40;

    public static void Apply(IReadOnlyList<AnalyzedFile> files)
    {
        var diffs = files.Where(f => f.SemanticDiff is not null).ToList();

        // Removed members, by kind and exact tokens, in a stable order.
        var removed = new Dictionary<(string Kind, string Tokens), Queue<(AnalyzedFile File, MemberChange Change)>>();
        foreach (var file in diffs)
            foreach (var change in file.SemanticDiff!.Changes.Where(IsCandidate(MemberChangeKind.Removed)))
            {
                var key = (change.MemberKind, change.Tokens!);
                if (!removed.TryGetValue(key, out var queue)) removed[key] = queue = new();
                queue.Enqueue((file, change));
            }
        if (removed.Count == 0) return;

        var replacements = new Dictionary<AnalyzedFile, Dictionary<MemberChange, MemberChange>>();
        void Replace(AnalyzedFile file, MemberChange from, MemberChange to)
        {
            if (!replacements.TryGetValue(file, out var map)) replacements[file] = map = new(ReferenceEqualityComparer.Instance);
            map[from] = to;
        }

        foreach (var file in diffs)
        {
            foreach (var added in file.SemanticDiff!.Changes.Where(IsCandidate(MemberChangeKind.Added)))
            {
                if (!removed.TryGetValue((added.MemberKind, added.Tokens!), out var queue)) continue;
                // Must come from another file; same-file moves are already handled (not reported at all).
                var source = queue.FirstOrDefault(r => r.File != file);
                if (source.File is null) continue;
                RemoveFrom(queue, source);

                Replace(file, added, added with
                {
                    Change = MemberChangeKind.Moved,
                    OldDisplayName = source.Change.DisplayName,
                    MovedPath = source.File.Path,
                });
                Replace(source.File, source.Change, source.Change with
                {
                    Change = MemberChangeKind.Moved,
                    OldDisplayName = added.DisplayName,
                    MovedPath = file.Path,
                });
            }
        }

        foreach (var (file, map) in replacements)
        {
            var diff = file.SemanticDiff!;
            file.ReplaceSemanticDiff(diff with { Changes = diff.Changes.Select(c => map.TryGetValue(c, out var moved) ? moved : c).ToList() });
        }
    }

    /// <summary>Type headers qualify at any size: a type's name is distinctive even when its header is short.</summary>
    static Func<MemberChange, bool> IsCandidate(MemberChangeKind kind) =>
        c => c.Change == kind && c.Tokens is { } tokens &&
             (tokens.Length >= MinTokenChars || SemanticDiffer.TypeKinds.Contains(c.MemberKind));

    static void RemoveFrom<T>(Queue<T> queue, T item)
    {
        var rest = queue.Where(x => !EqualityComparer<T>.Default.Equals(x, item)).ToList();
        queue.Clear();
        foreach (var x in rest) queue.Enqueue(x);
    }
}
