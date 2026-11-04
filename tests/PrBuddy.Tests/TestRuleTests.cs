using PrBuddy.Core.Rules.Testing;

namespace PrBuddy.Tests;

public class TestRuleTests
{
    const string TestPath = "tests/Demo.Tests/CalculatorTests.cs";

    [Fact]
    public void MissingTests_flags_behavior_change_with_no_test_changes()
    {
        var prod = Pr.File("src/Calculator.cs", "class C { int A() => 1; }", "class C { int A() => 2; }");
        var test = Pr.File(TestPath, "class T { }", "class T { void X() { } }");

        var finding = Assert.Single(Pr.Run(new MissingTestsRule(), prod));
        Assert.Null(finding.Path);
        Assert.Empty(Pr.Run(new MissingTestsRule(), prod, test));
    }

    [Fact]
    public void MissingTests_ignores_cosmetic_changes()
    {
        var prod = Pr.File("src/Calculator.cs", "class C { int A() => 1; }", "class C\n{\n    int A() => 1;\n}");
        Assert.Empty(Pr.Run(new MissingTestsRule(), prod));
    }

    [Fact]
    public void WeakenedTests_flags_dropped_assertions_and_removed_tests()
    {
        const string before = """
            public class CalculatorTests
            {
                [Fact]
                public void Adds()
                {
                    Assert.Equal(3, Calc.Add(1, 2));
                    Assert.Equal(0, Calc.Add(0, 0));
                }

                [Fact]
                public void Subtracts() => Assert.Equal(1, Calc.Sub(2, 1));

                [Fact]
                public void Multiplies() => Assert.Equal(4, Calc.Mul(2, 2));
            }
            """;
        const string after = """
            public class CalculatorTests
            {
                [Fact]
                public void Adds()
                {
                    Assert.Equal(3, Calc.Add(1, 2));
                }

                [Fact]
                public void Multiplication_works() => Assert.Equal(4, Calc.Mul(2, 2));
            }
            """;

        var findings = Pr.Run(new WeakenedTestsRule(), Pr.File(TestPath, before, after));

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.Line == 4 && f.Message.Contains("from 2 assertion(s) to 1"));
        Assert.Contains(findings, f => f.Line is null && f.Message.Contains("Subtracts"));
        // Multiplies was renamed with an identical body, so it isn't reported as removed.
    }

    [Fact]
    public void SkippedTest_flags_newly_skipped_tests()
    {
        const string before = """
            public class T
            {
                [Fact]
                public void A() => Assert.True(Run());
            }
            """;
        var after = before.Replace("[Fact]", "[Fact(Skip = \"flaky\")]");

        var finding = Assert.Single(Pr.Run(new SkippedTestRule(), Pr.File(TestPath, before, after)));
        Assert.Equal(3, finding.Line);
    }

    [Fact]
    public void TestWithoutAssertions_flags_tests_that_cannot_fail()
    {
        const string after = """
            public class T
            {
                [Fact]
                public void NoAssert() { var x = Calc.Add(1, 2); }

                [Fact]
                public void Trivial() { Calc.Add(1, 2); Assert.True(true); }

                [Fact]
                public void Real() => Assert.Equal(3, Calc.Add(1, 2));

                [Fact]
                public void Fluent() => Calc.Add(1, 2).Should().Be(3);

                [Fact]
                public void RealPlusTrivial() { Assert.Equal(3, Calc.Add(1, 2)); Assert.Equal(x, x); }
            }
            """;

        var findings = Pr.Run(new TestWithoutAssertionsRule(), Pr.File(TestPath, null, after));

        Assert.Equal([4, 7, 16], findings.Select(f => f.Line!.Value));
        Assert.Contains("doesn't assert anything", findings[0].Message);
        Assert.Contains("always pass", findings[1].Message);
        Assert.Contains("Assert.Equal(x, x)", findings[2].Message);
    }

    [Fact]
    public void TestWithoutAssertions_ignores_untouched_tests()
    {
        const string before = """
            public class T
            {
                [Fact]
                public void Old() { Run(); }
            }
            """;
        var after = before.Replace("}\n}", "}\n    void Helper() { }\n}");

        Assert.Empty(Pr.Run(new TestWithoutAssertionsRule(), Pr.File(TestPath, before, after)));
    }
}
