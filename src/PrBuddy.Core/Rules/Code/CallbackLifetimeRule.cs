using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

public sealed class CallbackLifetimeRule : RuleBase
{
    public override RuleDescriptor Descriptor { get; } =
        new("PRB017", "Lifecycle-sensitive access during callback", Severity.Warning, "Code");

    public override IEnumerable<Finding> Analyze(PullRequestContext context)
    {
        foreach (var file in context.CSharpFiles)
        {
            if (file.NewTree?.GetRoot() is not { } root || file.File.AddedLines.Count == 0) continue;
            var types = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                .Where(type => (type.BaseList is not null ||
                    type.Members.OfType<MethodDeclarationSyntax>().Any(m => m.Identifier.ValueText == "Dispose")) &&
                    HasCallbackEntry(type)).ToArray();
            if (types.Length == 0) continue;
            var baseNames = types.SelectMany(type => type.BaseList?.Types.Select(b => b.Type) ?? [])
                .Select(BaseName).OfType<string>().ToHashSet(StringComparer.Ordinal);
            var related = baseNames.Count == 0 ? [] : context.RepoTrees()
                .Where(f => f.Tree != root.SyntaxTree && baseNames.Contains(Path.GetFileNameWithoutExtension(f.Path)))
                .Select(f => f.Tree);
            var model = LocalSemantics.Create(root.SyntaxTree, related);
            var reported = new HashSet<int>();
            foreach (var type in types)
            foreach (var access in FindAccesses(type, model))
            {
                var line = LocalSemantics.AddedLine(file, access.Call) ?? LocalSemantics.AddedLine(file, access.Entry) ??
                    LocalSemantics.AddedLine(file, access.Trigger);
                if (line is null || !reported.Add(line.Value)) continue;
                yield return Report(file.Path, line, $"'{access.Target}' requires a live instance, but this callback path " +
                    "can run after disposal without rechecking the lifecycle state. Recheck the state immediately " +
                    "before the access or use an accessor that handles a stopped instance.");
            }
        }
    }

    static bool HasCallbackEntry(TypeDeclarationSyntax type) => type.Members.OfType<MethodDeclarationSyntax>()
        .SelectMany(m => m.DescendantNodes(LocalSemantics.DescendInto))
        .Any(n => n.IsKind(SyntaxKind.AddAssignmentExpression) ||
            n is ExpressionStatementSyntax { Expression: ConditionalAccessExpressionSyntax } ||
            n is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax } statement &&
            statement.Parent is BlockSyntax block && block.Statements.Count > 1);

    static string? BaseName(TypeSyntax type) => type switch
    {
        SimpleNameSyntax name => name.Identifier.ValueText,
        QualifiedNameSyntax name => name.Right.Identifier.ValueText,
        AliasQualifiedNameSyntax name => name.Name.Identifier.ValueText,
        _ => null
    };

    static IEnumerable<Access> FindAccesses(TypeDeclarationSyntax type, SemanticModel model)
    {
        var methods = type.Members.OfType<MethodDeclarationSyntax>().ToArray();
        var subscriptions = methods.SelectMany(m => m.DescendantNodes(LocalSemantics.DescendInto))
            .OfType<AssignmentExpressionSyntax>().Where(a => a.IsKind(SyntaxKind.AddAssignmentExpression))
            .ToArray();
        var invalidatedFields = InvalidatedFields(model.GetDeclaredSymbol(type), model.Compilation);
        if (invalidatedFields.Count == 0) yield break;

        foreach (var method in methods)
        {
            var subscription = subscriptions.FirstOrDefault(s => Subscribes(s, method, methods, model));
            foreach (var entry in SelfCalls(method))
            {
                var barrier = PrecedingCallback(entry, model);
                if (((SyntaxNode?)barrier ?? subscription) is not { } trigger) continue;
                foreach (var call in AccessCalls(entry, model))
                {
                    if (LocalSemantics.ResolveCall(model, call) is not { IsStatic: false } target ||
                        RequiredState(target, model.Compilation) is not { } invalid ||
                        !invalidatedFields.Contains(invalid) ||
                        IsGuarded(entry, invalid, model, barrier) ||
                        call != entry && IsGuarded(call, invalid, model, null))
                        continue;
                    yield return new Access(entry, call, trigger, target.Name);
                }
            }
        }
    }

    static bool Subscribes(AssignmentExpressionSyntax subscription, MethodDeclarationSyntax method,
        MethodDeclarationSyntax[] methods, SemanticModel model)
    {
        if (model.GetSymbolInfo(subscription.Right).Symbol is { } symbol)
            return LocalSemantics.Same(symbol, model.GetDeclaredSymbol(method));
        // The publisher's assembly might be missing. A unique instance method group on the
        // right of a member subscription is still distinguishable from a numeric +=.
        return subscription.Left is MemberAccessExpressionSyntax &&
            subscription.Right is IdentifierNameSyntax name && name.Identifier.ValueText == method.Identifier.ValueText &&
            method.ParameterList.Parameters.Count == 2 && !method.Modifiers.Any(SyntaxKind.StaticKeyword) &&
            methods.Count(m => m.Identifier.ValueText == method.Identifier.ValueText) == 1;
    }

    static StatementSyntax? PrecedingCallback(InvocationExpressionSyntax access, SemanticModel model) =>
        access.Ancestors().OfType<StatementSyntax>().Select(statement =>
            (statement.Parent as BlockSyntax)?.Statements.TakeWhile(s => s != statement)
                .OfType<ExpressionStatementSyntax>().LastOrDefault(s => InvokesCallback(s.Expression, model)))
            .FirstOrDefault(statement => statement is not null);

    static bool InvokesCallback(ExpressionSyntax expression, SemanticModel model)
    {
        var receiver = expression switch
        {
            ConditionalAccessExpressionSyntax
            {
                WhenNotNull: InvocationExpressionSyntax
                {
                    Expression: MemberBindingExpressionSyntax { Name.Identifier.ValueText: "Invoke" }
                }
            } conditional => conditional.Expression,
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Invoke" } member } => member.Expression,
            InvocationExpressionSyntax invocation => invocation.Expression,
            _ => null
        };
        return receiver is not null && (model.GetSymbolInfo(receiver).Symbol is IEventSymbol ||
            model.GetTypeInfo(receiver).Type?.TypeKind == TypeKind.Delegate);
    }

    static IEnumerable<InvocationExpressionSyntax> AccessCalls(InvocationExpressionSyntax entry, SemanticModel model)
    {
        yield return entry;
        if (LocalSemantics.ResolveCall(model, entry) is { IsStatic: false } target &&
            LocalSemantics.Declaration(target) is MethodDeclarationSyntax helper && helper.SyntaxTree == entry.SyntaxTree)
            foreach (var call in SelfCalls(helper))
                yield return call;
    }

    static IEnumerable<InvocationExpressionSyntax> SelfCalls(MethodDeclarationSyntax method) =>
        method.DescendantNodes(LocalSemantics.DescendInto).OfType<InvocationExpressionSyntax>().Where(LocalSemantics.IsSelfCall);

    static HashSet<InvalidState> InvalidatedFields(INamedTypeSymbol? type, Compilation compilation)
    {
        var result = new HashSet<InvalidState>();
        for (; type is not null; type = type.BaseType)
        foreach (var dispose in type.GetMembers("Dispose").OfType<IMethodSymbol>().Where(m => !m.IsStatic && m.Parameters.Length == 0))
        {
            if (LocalSemantics.Declaration(dispose) is not MethodDeclarationSyntax declaration) continue;
            var model = compilation.GetSemanticModel(declaration.SyntaxTree);
            CollectAssignments(declaration, model, result);
            foreach (var call in SelfCalls(declaration))
                if (model.GetSymbolInfo(call).Symbol is IMethodSymbol { IsStatic: false } helper &&
                    LocalSemantics.Same(helper.ContainingType, type) &&
                    LocalSemantics.Declaration(helper) is MethodDeclarationSyntax body)
                    CollectAssignments(body, compilation.GetSemanticModel(body.SyntaxTree), result);
        }
        return result;
    }

    static void CollectAssignments(MethodDeclarationSyntax method, SemanticModel model, HashSet<InvalidState> states)
    {
        foreach (var assignment in method.DescendantNodes(LocalSemantics.DescendInto).OfType<AssignmentExpressionSyntax>())
            if (assignment.IsKind(SyntaxKind.SimpleAssignmentExpression) &&
                model.GetSymbolInfo(assignment.Left).Symbol is IFieldSymbol { IsStatic: false } field &&
                model.GetConstantValue(assignment.Right) is { HasValue: true, Value: bool value })
                states.Add(new InvalidState(field, value));
    }

    static InvalidState? RequiredState(IMethodSymbol target, Compilation compilation)
    {
        if (LocalSemantics.Declaration(target) is not MethodDeclarationSyntax { Body.Statements: [var first, ..] } declaration)
            return null;
        var model = compilation.GetSemanticModel(declaration.SyntaxTree);
        return first switch
        {
            IfStatementSyntax guard when IsThrow(guard.Statement) => State(guard.Condition, model),
            ExpressionStatementSyntax { Expression: InvocationExpressionSyntax call }
                when call.ArgumentList.Arguments.Count > 0 &&
                model.GetSymbolInfo(call).Symbol is IMethodSymbol { Name: "ThrowIf" } method &&
                LocalSemantics.Same(method.ContainingType, compilation.GetTypeByMetadataName("System.ObjectDisposedException"))
                => State(call.ArgumentList.Arguments[0].Expression, model),
            _ => null
        };
    }

    static bool IsThrow(StatementSyntax statement) =>
        statement is ThrowStatementSyntax or BlockSyntax { Statements: [ThrowStatementSyntax] };

    static bool IsGuarded(InvocationExpressionSyntax access, InvalidState invalid, SemanticModel model, StatementSyntax? after)
    {
        var position = after?.Span.End ?? 0;
        foreach (var node in access.Ancestors().TakeWhile(n => n is not MethodDeclarationSyntax))
        {
            if (node is IfStatementSyntax branch && branch.SpanStart >= position &&
                branch.Statement.Span.Contains(access.Span) && Matches(branch.Condition, invalid, model, false))
                return true;
            if (node is BinaryExpressionSyntax binary && binary.Left.SpanStart >= position && binary.Right.Span.Contains(access.Span) &&
                (binary.IsKind(SyntaxKind.LogicalAndExpression) && Matches(binary.Left, invalid, model, false) ||
                 binary.IsKind(SyntaxKind.LogicalOrExpression) && Matches(binary.Left, invalid, model, true)))
                return true;
            if (node is StatementSyntax statement && statement.Parent is BlockSyntax block &&
                block.Statements.TakeWhile(s => s != statement).OfType<IfStatementSyntax>().Any(guard =>
                    guard.SpanStart >= position &&
                    guard.Statement is ReturnStatementSyntax or ThrowStatementSyntax or BlockSyntax { Statements: [ReturnStatementSyntax or ThrowStatementSyntax] } &&
                    Matches(guard.Condition, invalid, model, true)))
                return true;
        }
        return false;
    }

    static bool Matches(ExpressionSyntax condition, InvalidState invalid, SemanticModel model, bool whenInvalid)
    {
        if (condition is ParenthesizedExpressionSyntax parentheses)
            return Matches(parentheses.Expression, invalid, model, whenInvalid);
        if (condition is BinaryExpressionSyntax binary &&
            binary.IsKind(whenInvalid ? SyntaxKind.LogicalOrExpression : SyntaxKind.LogicalAndExpression))
            return Matches(binary.Left, invalid, model, whenInvalid) || Matches(binary.Right, invalid, model, whenInvalid);
        return State(condition, model) is { } state && LocalSemantics.Same(state.Field, invalid.Field) &&
            (state.Value == invalid.Value) == whenInvalid;
    }

    static InvalidState? State(ExpressionSyntax condition, SemanticModel model)
    {
        var (expression, value) = BooleanOperand(condition);
        var symbol = model.GetSymbolInfo(expression).Symbol;
        if (symbol is IPropertySymbol property &&
            LocalSemantics.Declaration(property) is PropertyDeclarationSyntax { ExpressionBody.Expression: { } backing })
        {
            var (backingOperand, backingValue) = BooleanOperand(backing);
            symbol = model.Compilation.GetSemanticModel(backing.SyntaxTree).GetSymbolInfo(backingOperand).Symbol;
            value = value == backingValue;
        }
        return symbol is IFieldSymbol { IsStatic: false } field ? new InvalidState(field, value) : null;
    }

    static (ExpressionSyntax Operand, bool Value) BooleanOperand(ExpressionSyntax expression)
    {
        var value = true;
        while (expression is ParenthesizedExpressionSyntax ||
            expression is PrefixUnaryExpressionSyntax not && not.IsKind(SyntaxKind.LogicalNotExpression))
        {
            if (expression is ParenthesizedExpressionSyntax parentheses)
                expression = parentheses.Expression;
            else
            {
                expression = ((PrefixUnaryExpressionSyntax)expression).Operand;
                value = !value;
            }
        }
        return (expression, value);
    }

    readonly record struct InvalidState(IFieldSymbol Field, bool Value);
    sealed record Access(InvocationExpressionSyntax Entry, InvocationExpressionSyntax Call, SyntaxNode Trigger, string Target);
}
