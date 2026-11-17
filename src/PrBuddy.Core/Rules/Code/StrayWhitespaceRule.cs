namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB036: added C# lines with trailing spaces or tabs, or blank lines that aren't empty. Editors
/// strip them later, so they turn into diff noise in someone else's PR. One finding per file.
/// </summary>
public sealed class StrayWhitespaceRule : RuleBase
{
    public override RuleDescriptor Descriptor { get; } = new("PRB036", "Stray whitespace", Severity.Info, "Style");

    public override IEnumerable<Finding> Analyze(PullRequestContext context)
    {
        foreach (var file in context.CSharpFiles.Where(f => !f.IsGenerated && f.File.NewContent is not null))
        {
            var lines = file.AddedLineTexts()
                .Where(l => l.Text.Length > 0 && (l.Text[^1] == ' ' || l.Text[^1] == '\t'))
                .Select(l => l.Line).ToArray();
            if (lines.Length == 0) continue;

            var shown = string.Join(", ", lines.Take(5)) + (lines.Length > 5 ? ", ..." : "");
            yield return Report(file.Path, lines[0],
                (lines.Length == 1
                    ? $"Added line {shown} ends in spaces or tabs, or is a blank line that isn't empty. "
                    : $"{lines.Length} added lines end in spaces or tabs, or are blank lines that aren't empty (lines {shown}). ") +
                "Trim them, or set trim_trailing_whitespace = true in .editorconfig.");
        }
    }
}
