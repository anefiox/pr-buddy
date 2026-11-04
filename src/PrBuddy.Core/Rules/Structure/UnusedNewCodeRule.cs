using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Structure;

/// <summary>
/// PRB301: new code that nothing uses. Private members are checked against their own file (or the
/// whole repo for partial types); new internal types against the whole repo.
///
/// Syntax only, so it matches by name: a same-named identifier anywhere counts as a use. That keeps
/// it quiet rather than clever. Members with attributes, overrides, partial/extern members and
/// framework callbacks (Unity's Update, Godot's _Ready...) are skipped, because something outside
/// the code calls them.
/// </summary>
public sealed class UnusedNewCodeRule : RuleBase
{
    /// <summary>Private methods that engines and runtimes call by name.</summary>
    static readonly HashSet<string> FrameworkCallbacks = new(StringComparer.Ordinal)
    {
        // Unity
        "Awake", "Start", "Update", "FixedUpdate", "LateUpdate", "OnEnable", "OnDisable", "OnDestroy",
        "OnValidate", "Reset", "OnGUI", "OnApplicationQuit", "OnApplicationPause", "OnApplicationFocus",
        "OnTriggerEnter", "OnTriggerExit", "OnTriggerStay", "OnTriggerEnter2D", "OnTriggerExit2D", "OnTriggerStay2D",
        "OnCollisionEnter", "OnCollisionExit", "OnCollisionStay", "OnCollisionEnter2D", "OnCollisionExit2D", "OnCollisionStay2D",
        "OnMouseDown", "OnMouseUp", "OnMouseEnter", "OnMouseExit", "OnBecameVisible", "OnBecameInvisible",
        "OnDrawGizmos", "OnDrawGizmosSelected",
        // .NET
        "Main", "Dispose", "DisposeAsync", "Finalize", "ShouldSerialize", "OnDeserialized", "OnSerializing",
    };

    public override RuleDescriptor Descriptor { get; } = new("PRB301", "New code nothing uses", Severity.Warning, "Structure");

    public override IEnumerable<Finding> Analyze(PullRequestContext context)
    {
        Dictionary<string, int>? repoCounts = null;

        foreach (var file in context.CSharpFiles)
        {
            Dictionary<string, int>? fileCounts = null;
            foreach (var member in AddedMembers.In(file))
            {
                foreach (var (name, token, kind, scope) in Candidates(member))
                {
                    int uses;
                    if (scope == Scope.File)
                    {
                        fileCounts ??= CountIdentifiers([file.NewTree!]);
                        uses = fileCounts.GetValueOrDefault(name) - 1; // minus the declaration itself
                    }
                    else
                    {
                        repoCounts ??= CountIdentifiers(context.RepoTrees().Select(t => t.Tree));
                        uses = repoCounts.GetValueOrDefault(name) - 1;
                    }
                    if (uses > 0) continue;

                    var line = AnalyzedFile.LineOf(token);
                    yield return scope == Scope.File
                        ? Report(file.Path, line, $"New private {kind} '{AddedMembers.QualifiedName(member, name)}' isn't used anywhere in this file.")
                        : Report(file.Path, line, $"New internal {kind} '{name}' isn't referenced anywhere in the repository.", Severity.Info);
                }
            }
        }
    }

    enum Scope { File, Repo }

    static IEnumerable<(string Name, SyntaxToken Token, string Kind, Scope Scope)> Candidates(MemberDeclarationSyntax member)
    {
        if (member.AttributeLists.Count > 0) yield break;
        // C# 14 `extension(T x) { ... }` blocks have no name to look for; their members are called on x.
        if (member.IsKind(SyntaxKind.ExtensionBlockDeclaration)) yield break;
        var mods = member.Modifiers;
        if (mods.Any(SyntaxKind.OverrideKeyword) || mods.Any(SyntaxKind.ExternKeyword) ||
            mods.Any(SyntaxKind.PartialKeyword) || mods.Any(SyntaxKind.AbstractKeyword))
            yield break;

        // Top-level internal types: are they referenced anywhere?
        if (member is BaseTypeDeclarationSyntax type && type.Parent is not BaseTypeDeclarationSyntax)
        {
            if (IsInternal(mods) && type.Identifier.Text != "Program" && !ContainsMain(type) && !HoldsExtensions(type))
                yield return (type.Identifier.Text, type.Identifier, Keyword(type), Scope.Repo);
            yield break;
        }

        if (!IsPrivate(member)) yield break;
        // A partial type's other parts may live in other files.
        var scope = member.Ancestors().OfType<TypeDeclarationSyntax>().Any(t => t.Modifiers.Any(SyntaxKind.PartialKeyword))
            ? Scope.Repo
            : Scope.File;

        switch (member)
        {
            case MethodDeclarationSyntax m when m.ExplicitInterfaceSpecifier is null && !FrameworkCallbacks.Contains(m.Identifier.Text)
                                                && !m.Identifier.Text.StartsWith('_'): // Godot's _Ready, _Process...
                yield return (m.Identifier.Text, m.Identifier, "method", scope);
                break;
            case PropertyDeclarationSyntax p when p.ExplicitInterfaceSpecifier is null:
                yield return (p.Identifier.Text, p.Identifier, "property", scope);
                break;
            case FieldDeclarationSyntax f:
                foreach (var v in f.Declaration.Variables)
                    yield return (v.Identifier.Text, v.Identifier, "field", scope);
                break;
            case EventFieldDeclarationSyntax e:
                foreach (var v in e.Declaration.Variables)
                    yield return (v.Identifier.Text, v.Identifier, "event", scope);
                break;
            case BaseTypeDeclarationSyntax nested:
                yield return (nested.Identifier.Text, nested.Identifier, Keyword(nested), scope);
                break;
        }
    }

    /// <summary>Explicitly private, or no access modifier inside a class, struct or record (where that means private).</summary>
    static bool IsPrivate(MemberDeclarationSyntax member)
    {
        var mods = member.Modifiers;
        if (mods.Any(SyntaxKind.PublicKeyword) || mods.Any(SyntaxKind.InternalKeyword) || mods.Any(SyntaxKind.ProtectedKeyword))
            return false;
        return mods.Any(SyntaxKind.PrivateKeyword) || member.Parent is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax;
    }

    static bool IsInternal(SyntaxTokenList mods) =>
        mods.Any(SyntaxKind.InternalKeyword) ||
        !(mods.Any(SyntaxKind.PublicKeyword) || mods.Any(SyntaxKind.ProtectedKeyword) || mods.Any(SyntaxKind.PrivateKeyword) || mods.Any(SyntaxKind.FileKeyword));

    static bool ContainsMain(BaseTypeDeclarationSyntax type) =>
        type.DescendantNodes().OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.Text == "Main");

    /// <summary>Extension holders are used as `x.Method()`, so their own name rarely appears anywhere.</summary>
    static bool HoldsExtensions(BaseTypeDeclarationSyntax type) =>
        type is TypeDeclarationSyntax t && t.Members.Any(m =>
            m.IsKind(SyntaxKind.ExtensionBlockDeclaration) ||
            m is MethodDeclarationSyntax { ParameterList.Parameters: [var first, ..] } && first.Modifiers.Any(SyntaxKind.ThisKeyword));

    static string Keyword(BaseTypeDeclarationSyntax type) => type switch
    {
        TypeDeclarationSyntax t => t.Keyword.Text,
        EnumDeclarationSyntax => "enum",
        _ => "type",
    };

    /// <summary>How often each identifier appears (declarations included), across the given trees.</summary>
    static Dictionary<string, int> CountIdentifiers(IEnumerable<SyntaxTree> trees)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var tree in trees)
            foreach (var token in tree.GetRoot().DescendantTokens())
                if (token.IsKind(SyntaxKind.IdentifierToken))
                    counts[token.ValueText] = counts.GetValueOrDefault(token.ValueText) + 1;
        return counts;
    }
}
