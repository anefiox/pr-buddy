using System.Globalization;
using PrBuddy.Core;
using PrBuddy.Core.Git;
using PrBuddy.Core.Reporting;

namespace PrBuddy.Tests;

/// <summary>The same diff must give the same report on every machine, whatever its language settings.</summary>
public class CultureTests
{
    // A PR that produces percentages (PRB302, PRB303) and other numbers in its messages.
    static ChangedFile[] Pr_with_numbers()
    {
        const string original = """
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
        var copy = original.Replace("class Text", "class Urls").Replace("Slugify", "ToPart").Replace("ToLowerInvariant", "ToUpperInvariant");
        static string Fields(int n) => "class C {\n" + string.Concat(Enumerable.Range(0, n).Select(i => $"    int F{i};\n")) + "}\n";
        return
        [
            Pr.File("src/Text.cs", original, original),
            Pr.File("src/Auth/Urls.cs", null, copy),
            Pr.File("src/Auth/Login.cs", null, Fields(40)),
            Pr.File("src/Billing/Invoice.cs", null, Fields(2)),
        ];
    }

    static string Render(string culture)
    {
        var previous = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(culture);
            var report = PrAnalyzer.Analyze(Pr_with_numbers());
            var range = new DiffRange("main", "HEAD", new string('a', 40), new string('b', 40));
            return MarkdownReport.Render(range, report) + string.Join("\n", report.AllFindings.Select(f => f.Message));
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = previous;
        }
    }

    [Fact]
    public void Reports_are_identical_in_every_culture()
    {
        var english = Render("en-US");
        Assert.Contains("% similar to", english);       // the PR really produces percentages...
        Assert.Contains("% of the changed code", english);

        foreach (var culture in new[] { "", "de-DE", "fr-FR", "tr-TR", "ar-SA", "hi-IN" })
            Assert.Equal(english, Render(culture));      // ...and they read the same everywhere
    }

    [Theory]
    [InlineData(0.875, "88%")]
    [InlineData(0.8, "80%")]
    [InlineData(1.0, "100%")]
    [InlineData(0.945, "95%")] // halves round away from zero
    public void Percent_is_fixed_format(double share, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal(expected, Numbers.Percent(share));
            Assert.Equal("1.3B", PrBuddy.Core.Rules.Config.NewPackageRule.Downloads(1_250_000_000));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
