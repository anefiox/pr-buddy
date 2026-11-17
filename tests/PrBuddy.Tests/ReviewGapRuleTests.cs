using PrBuddy.Core;
using PrBuddy.Core.Rules.Code;
using PrBuddy.Core.Rules.Testing;

namespace PrBuddy.Tests;

/// <summary>Rules added after comparing PR Buddy with human reviews of open PRs.</summary>
public sealed class ReviewGapRuleTests
{
    static string Loader(string tryBody, string catchBody = "Error = e.Message;", string guard = "Result is not null || IsLoading") => $$"""
        using System;
        using System.Threading.Tasks;
        class Item
        {
            public object? Result { get; set; }
            public object? Versions { get; set; }
            public bool IsLoading { get; set; }
            public string? Error { get; set; }

            public async Task Load(Func<Task<Runner>> create)
            {
                if ({{guard}})
                {
                    return;
                }

                IsLoading = true;
                try
                {
                    var runner = await create();
                    {{tryBody}}
                }
                catch (Exception e)
                {
                    {{catchBody}}
                }
                finally
                {
                    IsLoading = false;
                }
            }
        }
        """;

    const string SetFirst = "Result = await runner.Show(); Versions = await runner.Index();";

    [Fact]
    public void ReportsGuardSetBeforeLaterWorkThatCanThrow()
    {
        var finding = Assert.Single(Pr.Run(new PrematureLoadGuardRule(), Pr.File("src/Item.cs", null, Loader(SetFirst))));
        Assert.Equal("PRB018", finding.RuleId);
        Assert.Contains("'Result'", finding.Message);
        Assert.Contains("line 12", finding.Message);
        Assert.Empty(Pr.Run(new PrematureLoadGuardRule(), Pr.File("src/Item.cs", Loader(SetFirst), Loader(SetFirst))));
    }

    [Theory]
    [InlineData("var show = await runner.Show(); Versions = await runner.Index(); Result = show;", "Error = e.Message;")]
    [InlineData(SetFirst, "Result = null;")]
    [InlineData("Versions = await runner.Index(); Result = await runner.Show();", "Error = e.Message;")]
    public void AllowsGuardSetLastOrResetInCatch(string tryBody, string catchBody) =>
        Assert.Empty(Pr.Run(new PrematureLoadGuardRule(), Pr.File("src/Item.cs", null, Loader(tryBody, catchBody))));

    [Fact]
    public void IgnoresGuardsOnLocalsAndParameters() =>
        Assert.Empty(Pr.Run(new PrematureLoadGuardRule(),
            Pr.File("src/Item.cs", null, Loader(SetFirst, guard: "create is null"))));

    static string Owner(string body, string field = "readonly HashSet<Node> _seen = [];") => $$"""
        using System.Collections.Generic;
        class Node { }
        class Owner
        {
            {{field}}
            {{body}}
        }
        """;

    [Fact]
    public void ReportsCollectionFieldThatIsNeverEmptied()
    {
        var after = Owner("public void Track(Node n) { _seen.Add(n); }");
        var finding = Assert.Single(Pr.Run(new GrowOnlyCollectionFieldRule(), Pr.File("src/Owner.cs", null, after)));
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("never removed", finding.Message);
    }

    [Fact]
    public void ReportsClearOnlyInAnotherMethodAsInfo()
    {
        var after = Owner("public void Track(Node n) { _seen.Add(n); } public void Release() { foreach (var n in _seen) { } _seen.Clear(); }");
        var finding = Assert.Single(Pr.Run(new GrowOnlyCollectionFieldRule(), Pr.File("src/Owner.cs", null, after)));
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("'Release'", finding.Message);
    }

    [Theory]
    [InlineData("public void Track(Node n) { _seen.Add(n); } public void Drop(Node n) { _seen.Remove(n); }", "readonly HashSet<Node> _seen = [];")]
    [InlineData("public void Track(string n) { _seen.Add(n); }", "readonly HashSet<string> _seen = [];")]
    [InlineData("public Node Get(string k) { if (_seen.TryGetValue(k, out var n)) return n; return _seen[k] = new Node(); }", "readonly Dictionary<string, Node> _seen = new();")]
    [InlineData("public Owner(Node n) { _seen.Add(n); }", "readonly HashSet<Node> _seen = [];")]
    [InlineData("public void Track(Node n) { _seen.Add(n); }", "public readonly HashSet<Node> _seen = [];")]
    public void AllowsRemovalCachesAndValueCollections(string body, string field) =>
        Assert.Empty(Pr.Run(new GrowOnlyCollectionFieldRule(), Pr.File("src/Owner.cs", null, Owner(body, field))));

    [Fact]
    public void IgnoresExistingFields()
    {
        var code = Owner("public void Track(Node n) { _seen.Add(n); }");
        Assert.Empty(Pr.Run(new GrowOnlyCollectionFieldRule(), Pr.File("src/Owner.cs", code, code)));
    }

    static string ReflectionTest(string lookup) => $$"""
        using System.Reflection;
        using Xunit;
        public class WidgetTests
        {
            [Fact]
            public void Works()
            {
                var method = {{lookup}};
                Assert.NotNull(method);
            }
        }
        """;

    [Fact]
    public void ReportsTestReflectingIntoNonPublicMember()
    {
        var after = ReflectionTest("typeof(Widget).GetMethod(\"SetValue\", BindingFlags.NonPublic | BindingFlags.Instance)");
        var finding = Assert.Single(Pr.Run(new ReflectionIntoPrivatesRule(), Pr.File("tests/WidgetTests.cs", null, after)));
        Assert.Equal("PRB105", finding.RuleId);
        Assert.Contains("'SetValue'", finding.Message);
    }

    [Theory]
    [InlineData("tests/WidgetTests.cs", "typeof(Widget).GetMethod(\"SetValue\")")]
    [InlineData("src/Widget.cs", "typeof(Widget).GetMethod(\"SetValue\", BindingFlags.NonPublic | BindingFlags.Instance)")]
    public void AllowsPublicLookupsAndProductionCode(string path, string lookup) =>
        Assert.Empty(Pr.Run(new ReflectionIntoPrivatesRule(), Pr.File(path, null, ReflectionTest(lookup))));
}

public sealed class FlagsEnumValueRuleTests
{
    static string Flags(string added, string attribute = "[Flags]") => $$"""
        using System;
        {{attribute}}
        public enum CommandFlags
        {
            None = 0,
            Buffered = 1,
            Pipelined = 2,
            NoCache = 4,
            {{added}}
        }
        """;

    [Fact]
    public void ReportsNumberThatOverlapsOtherFlags()
    {
        var finding = Assert.Single(Pr.Run(new FlagsEnumValueRule(),
            Pr.File("src/CommandFlags.cs", Flags(""), Flags("RegisterCancellation = 5,"))));
        Assert.Equal("PRB020", finding.RuleId);
        Assert.Contains("value 5", finding.Message);
        Assert.Contains("Buffered | NoCache", finding.Message);
        Assert.Contains("(8, or 1 << 3)", finding.Message);
    }

    [Fact]
    public void ReportsImplicitValueThatIsNotABit()
    {
        const string before = "[Flags] enum Mode { None, Read, Write }";
        var finding = Assert.Single(Pr.Run(new FlagsEnumValueRule(),
            Pr.File("src/Mode.cs", before, "[Flags] enum Mode { None, Read, Write, Execute }")));
        Assert.Contains("implicit value 3", finding.Message);
    }

    [Theory]
    [InlineData("RegisterCancellation = 8,", "[Flags]")]
    [InlineData("RegisterCancellation = 1 << 3,", "[System.FlagsAttribute]")]
    [InlineData("Default = Buffered | NoCache,", "[Flags]")]
    [InlineData("All = 7,", "[Flags]")]
    [InlineData("Everything = -1,", "[Flags]")]
    [InlineData("RegisterCancellation = 5,", "")]
    public void AllowsBitsCombinationsMasksAndPlainEnums(string added, string attribute) =>
        Assert.Empty(Pr.Run(new FlagsEnumValueRule(),
            Pr.File("src/CommandFlags.cs", Flags("", attribute), Flags(added, attribute))));

    [Fact]
    public void IgnoresExistingMembers()
    {
        var code = Flags("RegisterCancellation = 5,");
        Assert.Empty(Pr.Run(new FlagsEnumValueRule(), Pr.File("src/CommandFlags.cs", code, code)));
    }
}
