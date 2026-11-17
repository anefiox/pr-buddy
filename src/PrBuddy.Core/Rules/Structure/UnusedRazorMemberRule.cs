using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Structure;

/// <summary>
/// PRB305: a member added in a Razor component's @code block that nothing in the component, the
/// PR's other files or the repo's C# mentions. PR Buddy parses .cs files only, so without this the
/// unused-code check (PRB301) never sees component code. Members marked [Parameter], [Inject] and
/// the like, overrides and Razor lifecycle methods are skipped: the framework calls them.
/// </summary>
public sealed partial class UnusedRazorMemberRule : RuleBase
{
    public override RuleDescriptor Descriptor { get; } = new("PRB305", "Unused member in a Razor component", Severity.Info, "Structure");

    static readonly HashSet<string> FrameworkMethods = new(StringComparer.Ordinal)
    {
        "OnInitialized", "OnInitializedAsync", "OnParametersSet", "OnParametersSetAsync", "OnAfterRender", "OnAfterRenderAsync",
        "SetParametersAsync", "ShouldRender", "BuildRenderTree", "Dispose", "DisposeAsync",
    };

    public override IEnumerable<Finding> Analyze(PullRequestContext context)
    {
        var razorFiles = context.Files.Where(f => f.Path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase) && f.File.NewContent is not null).ToArray();
        if (razorFiles.Length == 0) yield break;

        // Everything else that might mention a member: the repo's C# (read once) and the PR's other files.
        var changed = context.Files.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        string? repoText = null;
        string Elsewhere(AnalyzedFile except) => (repoText ??= string.Join('\n', context.Snapshot.CSharpPaths
                .Where(p => !changed.Contains(p)).Take(5000).Select(p => context.Snapshot.Read(p) ?? ""))) + '\n' +
            string.Join('\n', context.Files.Where(f => f != except && f.File.NewContent is not null).Select(f => f.File.NewContent!));

        foreach (var file in razorFiles)
        {
            var text = file.File.NewContent!;
            string? others = null;
            foreach (var (block, startLine) in CodeBlocks(text))
            {
                var tree = CSharpSyntaxTree.ParseText("class __Component {\n" + block + "\n}", AnalyzedFile.ParseOptions);
                foreach (var (member, name) in Declarations(tree.GetRoot()))
                {
                    // Line 1 of the parsed text is the wrapper; the block's first line is startLine.
                    var line = member.GetLocation().GetLineSpan().StartLinePosition.Line - 1 + startLine;
                    if (!file.IsAddedLine(line) || Mentions(text, name) > 1 || Mentions(others ??= Elsewhere(file), name) > 0) continue;
                    yield return Report(file.Path, line,
                        $"'{name}' is new in this component's @code block, but nothing in the component or the rest of the code uses it. " +
                        "Remove it, or use it if it was meant to be.");
                }
            }
        }
    }

    static IEnumerable<(MemberDeclarationSyntax Member, string Name)> Declarations(SyntaxNode root)
    {
        foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            if (member is BaseTypeDeclarationSyntax || member.AttributeLists.Count > 0 ||
                member.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword)))
                continue;
            var name = member switch
            {
                PropertyDeclarationSyntax p => p.Identifier.ValueText,
                MethodDeclarationSyntax m when !FrameworkMethods.Contains(m.Identifier.ValueText) && m.ExplicitInterfaceSpecifier is null => m.Identifier.ValueText,
                FieldDeclarationSyntax { Declaration.Variables: [var v] } => v.Identifier.ValueText,
                _ => null,
            };
            if (name is not null) yield return (member, name);
        }
    }

    static int Mentions(string text, string name) => Regex.Matches(text, $@"(?<![\w@]){Regex.Escape(name)}(?!\w)").Count;

    /// <summary>The C# inside each @code { ... } or @functions { ... } block, with the 1-based line its first line is on.</summary>
    static IEnumerable<(string Code, int StartLine)> CodeBlocks(string text)
    {
        foreach (Match match in CodeBlockStart().Matches(text))
        {
            var open = match.Index + match.Length - 1;
            if (MatchingBrace(text, open) is { } close)
            {
                var start = open + 1;
                yield return (text[start..close], text[..start].Count(c => c == '\n') + 1);
            }
        }
    }

    /// <summary>The index of the brace that closes the one at <paramref name="open"/>, skipping strings, chars and comments.</summary>
    static int? MatchingBrace(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            var next = i + 1 < text.Length ? text[i + 1] : '\0';
            if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return i;
            else if (c == '/' && next == '/')
                i = text.IndexOf('\n', i) is var eol and >= 0 ? eol : text.Length;
            else if (c == '/' && next == '*')
                i = text.IndexOf("*/", i + 2, StringComparison.Ordinal) is var end and >= 0 ? end + 1 : text.Length;
            else if (c == '"' && i > 0 && text[i - 1] == '@')
            {
                // Verbatim string: "" is an escaped quote.
                for (i++; i < text.Length; i++)
                {
                    if (text[i] != '"') continue;
                    if (i + 1 < text.Length && text[i + 1] == '"') { i++; continue; }
                    break;
                }
            }
            else if (c is '"' or '\'')
            {
                for (i++; i < text.Length && text[i] != c && text[i] != '\n'; i++)
                    if (text[i] == '\\') i++;
            }
        }
        return null;
    }

    [GeneratedRegex(@"@(?:code|functions)\s*\{")]
    private static partial Regex CodeBlockStart();
}
