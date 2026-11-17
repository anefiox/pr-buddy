using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB019: a new collection field of objects that the type adds to but never removes from.
/// It keeps every item alive for the owner's lifetime. When the only removal is Clear() in another
/// method, items live until that call (info).
/// </summary>
public sealed class GrowOnlyCollectionFieldRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } =
        new("PRB019", "Collection field that only grows", Severity.Warning, "Code");

    static readonly HashSet<string> CollectionTypes = new(StringComparer.Ordinal)
    {
        "List", "HashSet", "SortedSet", "Dictionary", "SortedDictionary", "ConcurrentDictionary",
        "ConcurrentBag", "ConcurrentQueue", "Queue", "Stack", "LinkedList", "Collection", "ObservableCollection",
    };

    static readonly HashSet<string> Adds = new(StringComparer.Ordinal)
        { "Add", "TryAdd", "AddRange", "Enqueue", "Push", "Insert", "AddLast", "AddFirst", "GetOrAdd", "AddOrUpdate" };

    static readonly HashSet<string> Removes = new(StringComparer.Ordinal)
    {
        "Remove", "TryRemove", "RemoveAll", "RemoveWhere", "RemoveAt", "RemoveRange", "Dequeue", "TryDequeue",
        "Pop", "TryPop", "TryTake", "RemoveFirst", "RemoveLast", "ExceptWith", "IntersectWith", "TrimExcess",
    };

    static readonly HashSet<string> Lookups = new(StringComparer.Ordinal)
        { "TryGetValue", "ContainsKey", "Contains", "GetValueOrDefault" };

    static readonly HashSet<string> ValueTypes = new(StringComparer.Ordinal)
    {
        "string", "int", "long", "short", "byte", "bool", "char", "double", "float", "decimal", "uint", "ulong",
        "Guid", "DateTime", "TimeSpan", "Type", "nint",
    };

    protected override bool AppliesTo(AnalyzedFile file) => !file.IsTestFile;

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
        foreach (var field in type.Members.OfType<FieldDeclarationSyntax>())
        {
            if (field.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword) || m.IsKind(SyntaxKind.ConstKeyword)) ||
                field.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.ProtectedKeyword)) ||
                !file.IsNew(field) || ElementTypes(field.Declaration.Type) is not { } elements ||
                elements.All(e => ValueTypes.Contains(e)))
                continue;

            foreach (var variable in field.Declaration.Variables)
            {
                var name = variable.Identifier.ValueText;
                var uses = type.DescendantNodes().OfType<IdentifierNameSyntax>()
                    .Where(id => id.Identifier.ValueText == name && id.Parent is not VariableDeclaratorSyntax).ToArray();

                SyntaxNode? firstAdd = null;
                var clears = new List<InvocationExpressionSyntax>();
                var lookups = new List<InvocationExpressionSyntax>();
                var removed = false;
                foreach (var use in uses)
                {
                    var target = use.Parent is MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } m && m.Name == use
                        ? (ExpressionSyntax)m : use;
                    if (target.Parent is AssignmentExpressionSyntax assignment && assignment.Left == target ||
                        target.Parent is ArgumentSyntax || target.Parent is ReturnStatementSyntax ||
                        target.Parent is EqualsValueClauseSyntax || target.Parent is ArrowExpressionClauseSyntax)
                    {
                        removed = true; // reassigned or handed out: the type doesn't own its lifetime alone
                        break;
                    }
                    if (target.Parent is ElementAccessExpressionSyntax element && element.Expression == target &&
                        element.Parent is AssignmentExpressionSyntax set && set.Left == element)
                    {
                        firstAdd ??= element;
                        continue;
                    }
                    if (target.Parent is not MemberAccessExpressionSyntax access || access.Expression != target ||
                        access.Parent is not InvocationExpressionSyntax call)
                        continue;
                    var method = access.Name.Identifier.ValueText;
                    if (Adds.Contains(method)) firstAdd ??= call;
                    else if (Removes.Contains(method)) removed = true;
                    else if (method == "Clear") clears.Add(call);
                    else if (Lookups.Contains(method)) lookups.Add(call);
                }
                if (removed || firstAdd is null) continue;

                var addMethod = Enclosing(firstAdd);
                // A constructor fills it once; a lookup next to the add is a memo cache, which grows on purpose.
                if (addMethod is ConstructorDeclarationSyntax || lookups.Any(l => Enclosing(l) == addMethod)) continue;
                if (clears.Count == 0)
                {
                    yield return Report(file, variable,
                        $"New field '{name}' is added to (line {AnalyzedFile.LineOf(firstAdd)}) but never removed from or cleared, " +
                        "so it keeps every item alive as long as this object lives. Remove items when they're done, " +
                        "or use a weak or bounded store.");
                }
                else if (clears.All(c => Enclosing(c) != addMethod))
                {
                    var clearAt = Enclosing(clears[0]) is MethodDeclarationSyntax m ? $"'{m.Identifier.ValueText}'" : $"line {AnalyzedFile.LineOf(clears[0])}";
                    yield return Report(file, variable,
                        $"New field '{name}' is added to (line {AnalyzedFile.LineOf(firstAdd)}) and only emptied by Clear() in {clearAt}. " +
                        "Until that runs it holds a strong reference to every item it has seen, which can stop them " +
                        "being collected. Check that's intended, or remove items when they go away.", Severity.Info);
                }
            }
        }
    }

    static SyntaxNode? Enclosing(SyntaxNode node) =>
        node.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax);

    /// <summary>Element (and key) type names for a known collection type, or null.</summary>
    static string[]? ElementTypes(TypeSyntax type)
    {
        var generic = type switch
        {
            GenericNameSyntax g => g,
            QualifiedNameSyntax { Right: GenericNameSyntax g } => g,
            _ => null,
        };
        if (generic is null || !CollectionTypes.Contains(generic.Identifier.ValueText)) return null;
        return generic.TypeArgumentList.Arguments.Select(a => a switch
        {
            NullableTypeSyntax n => n.ElementType.ToString(),
            ArrayTypeSyntax array => array.ElementType.ToString(), // string[] holds no objects worth tracking either
            _ => a.ToString(),
        }).ToArray();
    }
}
