using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Semantics;

/// <summary>A declaration flattened into the strings the differ compares.</summary>
/// <param name="Key">Namespace + type path + name + parameter types. Identifies the member across versions.</param>
/// <param name="MatchName">Key without parameter types, to pair up a member whose parameters changed.</param>
/// <param name="Tokens">Every token without trivia. Equal tokens mean no semantic change.</param>
/// <param name="SignatureTokens">Tokens outside bodies and initializers: attributes, modifiers, types, parameters.</param>
/// <param name="BodyTokens">Tokens inside bodies and initializers, used to spot renames and moves.</param>
/// <param name="RawText">Tokens with trivia. Differs from another version with equal Tokens only by formatting or comments.</param>
internal sealed record MemberInfo(
    string Key,
    string MatchName,
    string DisplayName,
    string Kind,
    string Tokens,
    string SignatureTokens,
    string BodyTokens,
    string RawText,
    bool IsPublicApi,
    int Line);

internal static class MemberExtractor
{
    public static List<MemberInfo> Extract(SyntaxNode root)
    {
        var members = new List<MemberInfo>();

        var globals = root.ChildNodes().OfType<GlobalStatementSyntax>().ToList();
        if (globals.Count > 0)
        {
            var tokens = globals.SelectMany(g => g.DescendantTokens()).ToList();
            var joined = Join(tokens);
            members.Add(new MemberInfo("<top-level>", "<top-level>", "<top-level statements>", "statements",
                joined, "", joined, Raw(tokens), false, AnalyzedFile.LineOf(globals[0])));
        }

        // Only walk into namespaces and types: members never contain members, apart from nested types.
        foreach (var node in root.DescendantNodes(IsContainer).OfType<MemberDeclarationSyntax>())
        {
            var info = node switch
            {
                BaseNamespaceDeclarationSyntax or GlobalStatementSyntax => null,
                BaseTypeDeclarationSyntax type => FromTypeHeader(type),
                _ => FromMember(node),
            };
            if (info is not null) members.Add(info);
        }

        return members;
    }

    static bool IsContainer(SyntaxNode node) =>
        node is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax or BaseTypeDeclarationSyntax;

    /// <summary>The type declaration without its members: attributes, modifiers, name, base list, constraints.</summary>
    static MemberInfo FromTypeHeader(BaseTypeDeclarationSyntax type)
    {
        var tokens = type
            .DescendantTokens(n => n == type || n.Parent != type || n is not MemberDeclarationSyntax)
            // Enum member separators belong to the enum, so adding a member would look like a header change.
            .Where(t => !(t.IsKind(SyntaxKind.CommaToken) && t.Parent == type))
            .ToList();

        var path = TypePath(type);
        var keyword = type switch
        {
            TypeDeclarationSyntax t => t.Keyword.Text,
            EnumDeclarationSyntax e => e.EnumKeyword.Text,
            _ => "type",
        };
        var key = $"{Namespace(type)}::{path}::<type>";
        var joined = Join(tokens);
        return new MemberInfo(key, key, $"{keyword} {path}", keyword, joined, joined, "", Raw(tokens),
            IsPublicApi(type), AnalyzedFile.LineOf(type));
    }

    static MemberInfo? FromMember(MemberDeclarationSyntax node)
    {
        if (Describe(node) is not { } described) return null;
        var (name, parameters, kind) = described;

        var container = string.Join(".", node.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(TypeName));
        var qualified = container.Length > 0 ? $"{container}.{name}" : name;
        var ns = Namespace(node);

        var all = node.DescendantTokens().ToList();
        var signature = node.DescendantTokens(n => !IsBody(n)).ToList();
        var body = node.DescendantNodes(n => !IsBody(n)).Where(IsBody).SelectMany(b => b.DescendantTokens()).ToList();

        return new MemberInfo($"{ns}::{qualified}{parameters}", $"{ns}::{qualified}", qualified + parameters, kind,
            Join(all), Join(signature), Join(body), Raw(all), IsPublicApi(node), AnalyzedFile.LineOf(node));
    }

    static (string Name, string Parameters, string Kind)? Describe(MemberDeclarationSyntax node) => node switch
    {
        MethodDeclarationSyntax m => (Explicit(m.ExplicitInterfaceSpecifier) + m.Identifier.Text + TypeParameters(m.TypeParameterList), Parameters(m.ParameterList), "method"),
        ConstructorDeclarationSyntax c => ((c.Modifiers.Any(SyntaxKind.StaticKeyword) ? "static " : "") + c.Identifier.Text, Parameters(c.ParameterList), "constructor"),
        DestructorDeclarationSyntax d => ("~" + d.Identifier.Text, "()", "finalizer"),
        OperatorDeclarationSyntax o => ("operator " + o.OperatorToken.Text, Parameters(o.ParameterList), "operator"),
        ConversionOperatorDeclarationSyntax co => ($"{co.ImplicitOrExplicitKeyword.Text} operator {Compact(co.Type)}", Parameters(co.ParameterList), "operator"),
        PropertyDeclarationSyntax p => (Explicit(p.ExplicitInterfaceSpecifier) + p.Identifier.Text, "", "property"),
        IndexerDeclarationSyntax i => (Explicit(i.ExplicitInterfaceSpecifier) + "this", Parameters(i.ParameterList), "indexer"),
        EventDeclarationSyntax e => (Explicit(e.ExplicitInterfaceSpecifier) + e.Identifier.Text, "", "event"),
        EventFieldDeclarationSyntax ef => (Variables(ef.Declaration), "", "event"),
        FieldDeclarationSyntax f => (Variables(f.Declaration), "", "field"),
        DelegateDeclarationSyntax dd => (dd.Identifier.Text + TypeParameters(dd.TypeParameterList), Parameters(dd.ParameterList), "delegate"),
        EnumMemberDeclarationSyntax em => (em.Identifier.Text, "", "enum member"),
        _ => null,
    };

    /// <summary>Code that runs, as opposed to code that declares: bodies, expression bodies, initializers.</summary>
    static bool IsBody(SyntaxNode node) =>
        node is BlockSyntax or ArrowExpressionClauseSyntax or ConstructorInitializerSyntax
        || node is EqualsValueClauseSyntax { Parent: not ParameterSyntax };

    static bool IsPublicApi(MemberDeclarationSyntax node) =>
        IsVisibleOutsideAssembly(node) && node.Ancestors().OfType<BaseTypeDeclarationSyntax>().All(IsVisibleOutsideAssembly);

    static bool IsVisibleOutsideAssembly(MemberDeclarationSyntax member)
    {
        if (member is EnumMemberDeclarationSyntax) return true;
        var modifiers = member.Modifiers;
        if (modifiers.Any(SyntaxKind.PublicKeyword)) return true;
        if (modifiers.Any(SyntaxKind.ProtectedKeyword)) return !modifiers.Any(SyntaxKind.PrivateKeyword);
        if (modifiers.Any(SyntaxKind.PrivateKeyword) || modifiers.Any(SyntaxKind.InternalKeyword)) return false;
        return member.Parent is InterfaceDeclarationSyntax || IsExplicitImplementation(member);
    }

    static bool IsExplicitImplementation(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax { ExplicitInterfaceSpecifier: not null } => true,
        PropertyDeclarationSyntax { ExplicitInterfaceSpecifier: not null } => true,
        EventDeclarationSyntax { ExplicitInterfaceSpecifier: not null } => true,
        IndexerDeclarationSyntax { ExplicitInterfaceSpecifier: not null } => true,
        _ => false,
    };

    static string TypePath(BaseTypeDeclarationSyntax type) =>
        string.Join(".", type.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(TypeName));

    static string TypeName(BaseTypeDeclarationSyntax type) =>
        type.Identifier.Text + (type is TypeDeclarationSyntax t ? TypeParameters(t.TypeParameterList) : "");

    static string Namespace(SyntaxNode node) =>
        string.Join(".", node.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()));

    static string TypeParameters(TypeParameterListSyntax? list) => list is null ? "" : Compact(list);

    static string Explicit(ExplicitInterfaceSpecifierSyntax? specifier) =>
        specifier is null ? "" : Compact(specifier.Name) + ".";

    static string Variables(VariableDeclarationSyntax declaration) =>
        string.Join(", ", declaration.Variables.Select(v => v.Identifier.Text));

    static string Parameters(BaseParameterListSyntax list)
    {
        var (open, close) = list is BracketedParameterListSyntax ? ("[", "]") : ("(", ")");
        var parameters = list.Parameters.Select(p =>
            string.Join(" ", p.Modifiers.Select(m => m.Text).Append(p.Type is null ? p.Identifier.Text : Compact(p.Type))));
        return open + string.Join(", ", parameters) + close;
    }

    /// <summary>Node text with whitespace removed, so formatting never changes a key.</summary>
    static string Compact(SyntaxNode node) =>
        string.Concat(node.DescendantTokens().Select(t => t.Text)).Replace(",", ", ");

    static string Join(IEnumerable<SyntaxToken> tokens) => string.Join(" ", tokens.Select(t => t.Text));

    static string Raw(IEnumerable<SyntaxToken> tokens) => string.Concat(tokens.Select(t => t.ToFullString()));
}
