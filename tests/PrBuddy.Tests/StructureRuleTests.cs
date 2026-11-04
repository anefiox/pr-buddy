using PrBuddy.Core;
using PrBuddy.Core.Rules.Structure;

namespace PrBuddy.Tests;

public class StructureRuleTests
{
    static readonly Dictionary<string, string> NoOtherFiles = [];

    // ---------- PRB301: new code nothing uses ----------

    const string ServiceBefore = """
        public class Service
        {
            public int Run() => 1;
        }
        """;

    [Fact]
    public void Unused_new_private_members_are_reported()
    {
        const string after = """
            public class Service
            {
                private int _cache;
                public int Run() => 1;
                private int Helper() => 2;
                string Name { get; set; } = "";
            }
            """;

        var findings = Pr.Run(new UnusedNewCodeRule(), Pr.File("src/Service.cs", ServiceBefore, after));

        Assert.Equal([3, 5, 6], findings.Select(f => f.Line!.Value));
        Assert.Contains("private field 'Service._cache'", findings[0].Message);
        Assert.Contains("private method 'Service.Helper'", findings[1].Message);
        Assert.Contains("private property 'Service.Name'", findings[2].Message);
        Assert.All(findings, f => Assert.Equal(Severity.Warning, f.Severity));
    }

    [Fact]
    public void Used_new_private_members_are_fine()
    {
        const string after = """
            public class Service
            {
                private int _calls;
                public int Run() => Helper() + _calls++;
                private int Helper() => 2;
                private int ByName() => 3;
                public string Describe() => nameof(ByName);
            }
            """;

        Assert.Empty(Pr.Run(new UnusedNewCodeRule(), Pr.File("src/Service.cs", ServiceBefore, after)));
    }

    [Fact]
    public void Members_called_from_outside_the_code_are_skipped()
    {
        const string after = """
            using System.Text.RegularExpressions;
            public partial class Player : MonoBehaviour
            {
                void Update() { }
                void OnTriggerEnter(Collider other) { }
                public override void _Ready() { }
                void _Process(double delta) { }
                [SerializeField] private int speed;
                [Fact] void Test() { }
                private static partial Regex Digits();
                protected override void OnInit() { }
            }
            """;

        Assert.Empty(Pr.Run(new UnusedNewCodeRule(), Pr.File("src/Player.cs", null, after)));
    }

    [Fact]
    public void Existing_unused_code_is_not_the_prs_problem()
    {
        const string before = """
            public class Service
            {
                private int Old() => 1;
            }
            """;
        var after = before.Replace("private int Old() => 1;", "private int Old() => 2;");

        Assert.Empty(Pr.Run(new UnusedNewCodeRule(), Pr.File("src/Service.cs", before, after)));
    }

    [Fact]
    public void Private_members_of_partial_types_are_searched_across_the_repo()
    {
        const string after = """
            public partial class Form
            {
                private void Wire() { }
            }
            """;
        var otherPart = new Dictionary<string, string> { ["src/Form.Events.cs"] = "public partial class Form { void Load() => Wire(); }" };

        Assert.Empty(Pr.RunInRepo(new UnusedNewCodeRule(), otherPart, Pr.File("src/Form.cs", null, after)));
        Assert.Single(Pr.RunInRepo(new UnusedNewCodeRule(), NoOtherFiles, Pr.File("src/Form.cs", null, after)));
    }

    [Fact]
    public void New_internal_types_are_checked_across_the_repo()
    {
        const string helper = "internal static class PriceFormatter { public static string Format(decimal d) => d.ToString(); }";
        var users = new Dictionary<string, string> { ["src/Cart.cs"] = "class Cart { string P() => PriceFormatter.Format(1m); }" };

        Assert.Empty(Pr.RunInRepo(new UnusedNewCodeRule(), users, Pr.File("src/PriceFormatter.cs", null, helper)));

        var finding = Assert.Single(Pr.RunInRepo(new UnusedNewCodeRule(), NoOtherFiles, Pr.File("src/PriceFormatter.cs", null, helper)));
        Assert.Contains("New internal class 'PriceFormatter'", finding.Message);
        Assert.Equal(Severity.Info, finding.Severity);
    }

    [Theory]
    // From real repos: extension holders are used as x.Method(), never by their own name.
    [InlineData("internal static class DirectoryExtensions { public static int Depth(this string path) => 0; }")]
    [InlineData("internal static class DirectoryExtensions { extension(string path) { public int Depth => 0; } }")]
    public void Extension_holders_are_skipped(string code) =>
        Assert.Empty(Pr.Run(new UnusedNewCodeRule(), Pr.File("src/Extensions.cs", null, code)));

    [Fact]
    public void Public_types_and_program_entry_points_are_skipped()
    {
        Assert.Empty(Pr.Run(new UnusedNewCodeRule(), Pr.File("src/Api.cs", null, "public class Api { }")));
        Assert.Empty(Pr.Run(new UnusedNewCodeRule(), Pr.File("src/Program.cs", null, "class Program { static void Main() { } }")));
        Assert.Empty(Pr.Run(new UnusedNewCodeRule(), Pr.File("src/Entry.cs", null, "class Entry { static void Main() { } }")));
    }

    // ---------- PRB302: duplicated code ----------

    const string ExistingHelper = """
        namespace Shop;

        public static class Text
        {
            public static string Slugify(string input)
            {
                var builder = new System.Text.StringBuilder();
                foreach (var c in input.Trim().ToLowerInvariant())
                {
                    if (char.IsLetterOrDigit(c)) builder.Append(c);
                    else if (char.IsWhiteSpace(c) || c == '-') builder.Append('-');
                }
                return builder.ToString().Trim('-');
            }
        }
        """;

    // The same method with renamed locals and a different separator constant.
    const string CopiedHelper = """
        namespace Shop.Catalog;

        public class ProductUrls
        {
            public string ToUrlPart(string name)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var ch in name.Trim().ToLowerInvariant())
                {
                    if (char.IsLetterOrDigit(ch)) sb.Append(ch);
                    else if (char.IsWhiteSpace(ch) || ch == '_') sb.Append('_');
                }
                return sb.ToString().Trim('_');
            }
        }
        """;

    [Fact]
    public void A_renamed_copy_of_an_existing_method_is_reported()
    {
        var repo = new Dictionary<string, string> { ["src/Text.cs"] = ExistingHelper };

        var finding = Assert.Single(Pr.RunInRepo(new DuplicateCodeRule(), repo, Pr.File("src/Catalog/ProductUrls.cs", null, CopiedHelper)));

        Assert.Equal(5, finding.Line);
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("'ProductUrls.ToUrlPart' is 100% similar to 'Text.Slugify' (src/Text.cs:L5)", finding.Message);
    }

    [Fact]
    public void A_close_but_not_exact_copy_is_info()
    {
        // One call changed: still clearly derived from Slugify, but not a straight copy.
        var variant = CopiedHelper.Replace("ToLowerInvariant()", "ToUpperInvariant()");
        var repo = new Dictionary<string, string> { ["src/Text.cs"] = ExistingHelper };

        var finding = Assert.Single(Pr.RunInRepo(new DuplicateCodeRule(), repo, Pr.File("src/Catalog/ProductUrls.cs", null, variant)));
        Assert.Equal(Severity.Info, finding.Severity);
    }

    [Fact]
    public void Similar_test_methods_are_not_duplication()
    {
        string Test(string name, string input) => $$"""
                [Fact]
                public void {{name}}()
                {
                    var sut = new Shop.Text();
                    var result = Shop.Text.Slugify("{{input}}");
                    Assert.Equal("{{input}}", result);
                    Assert.NotNull(sut);
                    Assert.True(result.Length > 0 && result.Length < 100 && !result.Contains(' '));
                }
            """;
        var tests = $"public class TextTests\n{{\n{Test("A", "x")}\n{Test("B", "y")}\n}}";

        Assert.Empty(Pr.RunInRepo(new DuplicateCodeRule(), NoOtherFiles, Pr.File("tests/TextTests.cs", null, tests)));
    }

    [Fact]
    public void Different_methods_with_a_similar_shape_are_not_copies()
    {
        const string different = """
            public class Totals
            {
                public decimal Sum(System.Collections.Generic.List<decimal> prices)
                {
                    var total = 0m;
                    foreach (var p in prices)
                    {
                        if (p > 0) total += p;
                        else if (p < -1000) throw new System.ArgumentException("price");
                    }
                    return System.Math.Round(total, 2, System.MidpointRounding.AwayFromZero);
                }
            }
            """;
        var repo = new Dictionary<string, string> { ["src/Text.cs"] = ExistingHelper };

        Assert.Empty(Pr.RunInRepo(new DuplicateCodeRule(), repo, Pr.File("src/Totals.cs", null, different)));
    }

    [Fact]
    public void Short_methods_are_ignored()
    {
        var repo = new Dictionary<string, string> { ["src/A.cs"] = "class A { int Twice(int x) { return x * 2; } }" };
        Assert.Empty(Pr.RunInRepo(new DuplicateCodeRule(), repo, Pr.File("src/B.cs", null, "class B { int Double(int y) { return y * 2; } }")));
    }

    [Fact]
    public void Two_new_copies_in_one_pr_are_reported_once()
    {
        var second = CopiedHelper.Replace("ProductUrls", "CategoryUrls");
        var findings = Pr.RunInRepo(new DuplicateCodeRule(), NoOtherFiles,
            Pr.File("src/Catalog/ProductUrls.cs", null, CopiedHelper),
            Pr.File("src/Catalog/CategoryUrls.cs", null, second));

        Assert.Single(findings);
    }

    [Fact]
    public void Editing_an_existing_method_is_not_duplication()
    {
        // The method isn't new, so even though it matches its old self it isn't reported.
        var edited = ExistingHelper.Replace("Trim('-')", "Trim('-', ' ')");
        Assert.Empty(Pr.RunInRepo(new DuplicateCodeRule(), NoOtherFiles, Pr.File("src/Text.cs", ExistingHelper, edited)));
    }

    [Fact]
    public void Ignored_paths_are_left_out_of_the_search()
    {
        var files = new[] { Pr.File("src/Catalog/ProductUrls.cs", null, CopiedHelper) };
        var snapshot = new InMemorySnapshot(new Dictionary<string, string>
        {
            ["vendor/Text.cs"] = ExistingHelper,
            ["src/Catalog/ProductUrls.cs"] = CopiedHelper,
        });
        var context = new PullRequestContext(files.Select(f => new AnalyzedFile(f)).ToList(), snapshot, p => p.StartsWith("vendor/"));

        Assert.Empty(new DuplicateCodeRule().Analyze(context));
    }
}
