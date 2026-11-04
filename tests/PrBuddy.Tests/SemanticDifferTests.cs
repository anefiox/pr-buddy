using Microsoft.CodeAnalysis.CSharp;
using PrBuddy.Core.Semantics;

namespace PrBuddy.Tests;

public class SemanticDifferTests
{
    static FileSemanticDiff Diff(string before, string after) =>
        SemanticDiffer.Compare("Calculator.cs", CSharpSyntaxTree.ParseText(before), CSharpSyntaxTree.ParseText(after));

    const string Calculator = """
        namespace Demo
        {
            public class Calculator
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }

                public int Add(int a, int b, int c) => a + b + c;

                private int Twice(int x) => x * 2;
            }
        }
        """;

    [Fact]
    public void Formatting_and_comment_changes_are_cosmetic()
    {
        const string after = """
            namespace Demo
            {
                public class Calculator
                {
                    // Adds two numbers.
                    public int Add(int a,int b) { return a+b; }

                    public int Add(int a, int b, int c)
                        => a + b + c;

                    private int Twice(int x) => x * 2;
                }
            }
            """;

        var diff = Diff(Calculator, after);

        Assert.True(diff.IsCosmeticOnly);
        Assert.Equal(0, diff.RealChangeCount);
        Assert.Equal(2, diff.Changes.Count);
    }

    [Fact]
    public void File_scoped_namespace_conversion_is_cosmetic()
    {
        const string after = """
            namespace Demo;

            public class Calculator
            {
                public int Add(int a, int b)
                {
                    return a + b;
                }

                public int Add(int a, int b, int c) => a + b + c;

                private int Twice(int x) => x * 2;
            }
            """;

        Assert.True(Diff(Calculator, after).IsCosmeticOnly);
    }

    [Fact]
    public void Reordering_members_is_not_a_change()
    {
        const string after = """
            namespace Demo
            {
                public class Calculator
                {
                    private int Twice(int x) => x * 2;

                    public int Add(int a, int b, int c) => a + b + c;

                    public int Add(int a, int b)
                    {
                        return a + b;
                    }
                }
            }
            """;

        var diff = Diff(Calculator, after);
        Assert.True(diff.IsCosmeticOnly);
        Assert.Equal(0, diff.RealChangeCount);
    }

    [Fact]
    public void Body_change_in_one_overload_is_a_behavior_change_on_that_overload_only()
    {
        var after = Calculator.Replace("return a + b;", "return checked(a + b);");

        var change = Assert.Single(Diff(Calculator, after).Changes);
        Assert.Equal(MemberChangeKind.BehaviorChanged, change.Change);
        Assert.Equal("Calculator.Add(int, int)", change.DisplayName);
        Assert.True(change.IsPublicApi);
        Assert.Equal(5, change.Line);
    }

    [Fact]
    public void Parameter_type_change_is_a_signature_change()
    {
        var after = Calculator.Replace("private int Twice(int x)", "private int Twice(long x)");

        var change = Assert.Single(Diff(Calculator, after).Changes);
        Assert.Equal(MemberChangeKind.SignatureChanged, change.Change);
        Assert.Equal("Calculator.Twice(long)", change.DisplayName);
        Assert.Equal("Calculator.Twice(int)", change.OldDisplayName);
        Assert.False(change.IsPublicApi);
    }

    [Fact]
    public void Modifier_change_is_a_signature_change()
    {
        var after = Calculator.Replace("private int Twice", "public int Twice");

        var change = Assert.Single(Diff(Calculator, after).Changes);
        Assert.Equal(MemberChangeKind.SignatureChanged, change.Change);
        Assert.True(change.IsPublicApi);
    }

    [Fact]
    public void Same_body_under_a_new_name_is_a_rename()
    {
        var after = Calculator.Replace("Twice", "Double");

        var change = Assert.Single(Diff(Calculator, after).Changes);
        Assert.Equal(MemberChangeKind.Renamed, change.Change);
        Assert.Equal("Calculator.Double(int)", change.DisplayName);
        Assert.Equal("Calculator.Twice(int)", change.OldDisplayName);
    }

    [Fact]
    public void Added_and_removed_members_are_reported()
    {
        var after = Calculator
            .Replace("private int Twice(int x) => x * 2;", "public int Negate(int x) => -x;");

        var changes = Diff(Calculator, after).Changes;
        Assert.Contains(changes, c => c is { Change: MemberChangeKind.Added, DisplayName: "Calculator.Negate(int)" });
        Assert.Contains(changes, c => c is { Change: MemberChangeKind.Removed, DisplayName: "Calculator.Twice(int)", Line: null });
    }

    [Fact]
    public void Base_type_change_is_a_signature_change_on_the_type()
    {
        var after = Calculator.Replace("public class Calculator", "public class Calculator : System.IDisposable")
            .Replace("private int Twice", "public void Dispose() { }\n        private int Twice");

        var changes = Diff(Calculator, after).Changes;
        Assert.Contains(changes, c => c is { Change: MemberChangeKind.SignatureChanged, DisplayName: "class Calculator", MemberKind: "class" });
        Assert.Contains(changes, c => c is { Change: MemberChangeKind.Added, DisplayName: "Calculator.Dispose()" });
    }

    [Fact]
    public void Adding_an_enum_member_does_not_flag_the_enum_itself()
    {
        const string before = "public enum Color { Red, Green }";
        const string after = "public enum Color { Red, Green, Blue }";

        // "Green" also shows up as cosmetic: it lost the trailing space before "}".
        var change = Assert.Single(Diff(before, after).Changes, c => c.Change != MemberChangeKind.CosmeticOnly);
        Assert.Equal(MemberChangeKind.Added, change.Change);
        Assert.Equal("Color.Blue", change.DisplayName);
    }

    [Fact]
    public void Property_initializer_change_is_a_behavior_change()
    {
        const string before = "class Options { public int Retries { get; set; } = 3; }";
        const string after = "class Options { public int Retries { get; set; } = 5; }";

        var change = Assert.Single(Diff(before, after).Changes);
        Assert.Equal(MemberChangeKind.BehaviorChanged, change.Change);
        Assert.False(change.IsPublicApi); // the class is internal
    }

    [Theory]
    // Switching between expression and block bodies, or dropping an initializer, changes the body, not the signature.
    [InlineData("public int Count(int[] items) => items.Length;", "public int Count(int[] items) { try { return items.Length; } catch { } return 0; }")]
    [InlineData("public int Count(int[] items) { return items.Length; }", "public int Count(int[] items) => items.Length + 1;")]
    [InlineData("public int Size { get => _size; }", "public int Size { get { return _size * 2; } }")]
    [InlineData("public int Size => _size;", "public int Size { get { return _size + 1; } }")]
    [InlineData("public int Retries { get; set; } = 3;", "public int Retries { get; set; }")]
    public void Body_style_changes_are_behavior_not_signature(string before, string after)
    {
        var change = Assert.Single(Diff($"class C {{ int _size; {before} }}", $"class C {{ int _size; {after} }}").Changes);
        Assert.Equal(MemberChangeKind.BehaviorChanged, change.Change);
    }

    [Fact]
    public void Same_body_in_another_style_is_cosmetic_or_behavior_never_signature()
    {
        // `=> x` and `{ return x; }` are different tokens, so it isn't cosmetic, but it's not a signature change either.
        var change = Assert.Single(Diff("class C { public int A() => 1; }", "class C { public int A() { return 1; } }").Changes);
        Assert.NotEqual(MemberChangeKind.SignatureChanged, change.Change);
    }

    [Fact]
    public void Members_of_an_internal_type_are_not_public_api()
    {
        var before = Calculator.Replace("public class", "class");
        var after = before.Replace("return a + b;", "return b + a;");

        Assert.False(Assert.Single(Diff(before, after).Changes).IsPublicApi);
    }

    [Fact]
    public void New_file_reports_every_member_as_added()
    {
        var diff = SemanticDiffer.Compare("Calculator.cs", null, CSharpSyntaxTree.ParseText(Calculator));

        Assert.All(diff.Changes, c => Assert.Equal(MemberChangeKind.Added, c.Change));
        Assert.Equal(4, diff.Changes.Count); // the class header plus three methods
    }
}
