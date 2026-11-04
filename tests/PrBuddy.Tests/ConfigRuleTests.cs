using PrBuddy.Core;
using PrBuddy.Core.Packages;
using PrBuddy.Core.Rules.Config;

namespace PrBuddy.Tests;

public class ConfigRuleTests
{
    const string Project = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Serilog" Version="3.0.0" />
          </ItemGroup>
        </Project>
        """;

    [Fact]
    public void NewPackage_flags_new_packages_but_not_version_bumps()
    {
        var after = Project
            .Replace("Version=\"3.0.0\"", "Version=\"4.0.0\"")
            .Replace("  </ItemGroup>", "    <PackageReference Include=\"Newtonsoft.Jsonn\" Version=\"13.0.1\" />\n  </ItemGroup>");

        var finding = Assert.Single(Pr.Run(new NewPackageRule(), Pr.File("src/App/App.csproj", Project, after)));
        Assert.Contains("Newtonsoft.Jsonn", finding.Message);
        Assert.Equal(7, finding.Line);
    }

    [Fact]
    public void NewPackage_summarizes_a_brand_new_project_in_one_finding()
    {
        const string testProject = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="coverlet.collector" Version="6.0.4" />
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
                <PackageReference Include="xunit" Version="2.9.3" />
              </ItemGroup>
            </Project>
            """;

        var finding = Assert.Single(Pr.Run(new NewPackageRule(), Pr.File("tests/A.Tests/A.Tests.csproj", null, testProject)));
        Assert.Equal(3, finding.Line);
        Assert.Contains("3 package(s): coverlet.collector, Microsoft.NET.Test.Sdk, xunit", finding.Message);
    }

    [Fact]
    public void NewPackage_new_project_without_packages_is_silent() =>
        Assert.Empty(Pr.Run(new NewPackageRule(), Pr.File("src/A/A.csproj", null, Project.Replace("<PackageReference Include=\"Serilog\" Version=\"3.0.0\" />", ""))));

    sealed class FakeRegistry(params PackageInfo?[] packages) : IPackageRegistry
    {
        public List<string> Asked { get; } = [];
        public PackageInfo? Lookup(string id)
        {
            Asked.Add(id);
            return packages.FirstOrDefault(p => string.Equals(p?.Id, id, StringComparison.OrdinalIgnoreCase))
                ?? (Unreachable ? null : new PackageInfo(id, Exists: false));
        }
        public bool Unreachable { get; init; }
    }

    static string WithPackages(params string[] ids) =>
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n"
        + string.Concat(ids.Select(id => $"    <PackageReference Include=\"{id}\" Version=\"1.0.0\" />\n"))
        + "  </ItemGroup>\n</Project>\n";

    [Fact]
    public void Online_known_packages_drop_to_info_and_suspicious_ones_say_why()
    {
        var registry = new FakeRegistry(
            new PackageInfo("Serilog", true, 1_234_000_000, Verified: true),
            new PackageInfo("Tiny.Helpers", true, 312));
        var before = WithPackages("Existing");
        var after = WithPackages("Existing", "Serilog", "Newtonsoft.Jsonn", "Tiny.Helpers");

        var findings = Pr.Run(new NewPackageRule(registry), Pr.File("src/A/A.csproj", before, after));

        Assert.Equal(3, findings.Count);
        Assert.Contains(findings, f => f is { Severity: Severity.Info, Line: 4 } && f.Message.Contains("Serilog (1.2B downloads, verified owner)"));
        Assert.Contains(findings, f => f is { Severity: Severity.Warning, Line: 5 } && f.Message.Contains("'Newtonsoft.Jsonn' isn't on nuget.org"));
        Assert.Contains(findings, f => f is { Severity: Severity.Warning, Line: 6 } && f.Message.Contains("'Tiny.Helpers' has only 312 downloads"));
        Assert.DoesNotContain("Existing", registry.Asked); // only new packages are looked up
    }

    [Fact]
    public void Online_new_project_reports_suspicious_packages_individually()
    {
        var registry = new FakeRegistry(new PackageInfo("xunit", true, 900_000_000));
        var findings = Pr.Run(new NewPackageRule(registry), Pr.File("tests/A.Tests/A.Tests.csproj", null, WithPackages("xunit", "xunit.runer")));

        Assert.Contains(findings, f => f.Severity == Severity.Info && f.Message.Contains("xunit (900M downloads)"));
        Assert.Contains(findings, f => f.Severity == Severity.Warning && f.Message.Contains("'xunit.runer' isn't on nuget.org"));
    }

    [Fact]
    public void Online_unreachable_registry_falls_back_to_a_plain_warning()
    {
        var registry = new FakeRegistry { Unreachable = true };
        var finding = Assert.Single(Pr.Run(new NewPackageRule(registry), Pr.File("src/A/A.csproj", WithPackages(), WithPackages("Serilog"))));
        Assert.Equal(Severity.Warning, finding.Severity);
        Assert.Contains("Couldn't reach nuget.org", finding.Message);
    }

    [Theory]
    [InlineData("Newtonsoft.Jsonn", "Newtonsoft.Json")]   // extra letter
    [InlineData("Newtonsof.Json", "Newtonsoft.Json")]     // dropped letter
    [InlineData("Newtonsoft.Josn", "Newtonsoft.Json")]    // swapped letters
    [InlineData("NewtonsoftJson", "Newtonsoft.Json")]     // separator dropped
    [InlineData("Newt0nsoft.Json", "Newtonsoft.Json")]    // zero for o
    [InlineData("Seri1og", "Serilog")]                    // one for l
    [InlineData("AutoMaper", "AutoMapper")]
    public void Lookalikes_of_popular_packages_are_caught_offline(string typo, string popular) =>
        Assert.Equal(popular, PackageLookalikes.LookalikeOf(typo));

    [Theory]
    [InlineData("Newtonsoft.Json")]          // the real thing
    [InlineData("newtonsoft.json")]          // NuGet ids are case-insensitive
    [InlineData("Moqq")]                     // too short to judge
    [InlineData("Acme.Internal.Billing")]    // nothing like any popular package
    public void Real_and_unrelated_names_are_not_lookalikes(string id) =>
        Assert.Null(PackageLookalikes.LookalikeOf(id));

    [Theory]
    // Real packages with 6.7M+ downloads each that an earlier version wrongly flagged.
    [InlineData("bunit.core", "xunit.core")]                                  // different first letter: another brand
    [InlineData("TUnit.Core", "xunit.core")]
    [InlineData("XunitXml.TestLogger", "JunitXml.TestLogger")]
    [InlineData("NUnitTestAdapter", "NUnit3TestAdapter")]                       // a version digit
    [InlineData("runtime.win7-x64.runtime.native.System.Data.SqlClient.sni", "runtime.win-x64.runtime.native.System.Data.SqlClient.sni")]
    [InlineData("Humanizer.Core.lt", "Humanizer.Core.pt")]                     // culture codes
    [InlineData("Contoso.Net8.Tools", "Contoso.Net6.Tools")]
    public void Legitimately_similar_names_are_not_lookalikes(string id, string popular) =>
        Assert.Null(PackageLookalikes.LookalikeOf(id, [popular]));

    [Fact]
    public void Popular_list_is_embedded_and_sane()
    {
        var list = PackageLookalikes.PopularPackages;
        Assert.InRange(list.Count, 500, 5000);
        Assert.Contains("Newtonsoft.Json", list);
        // nuget.org's empty search hides these; the refresh script has to find them some other way.
        Assert.Contains("Microsoft.NET.Test.Sdk", list);
        Assert.Contains("xunit.runner.visualstudio", list);
        Assert.DoesNotContain(list, id => id.StartsWith('#') || id.Length == 0);
    }

    [Fact]
    public void Offline_lookalike_gets_its_own_warning_naming_the_popular_package()
    {
        var findings = Pr.Run(new NewPackageRule(), Pr.File("src/A/A.csproj", WithPackages(), WithPackages("Newtonsoft.Jsonn", "Acme.Internal")));

        Assert.Contains(findings, f => f.Line == 3 && f.Message.Contains("one keystroke away from the popular 'Newtonsoft.Json'"));
        Assert.Contains(findings, f => f.Line == 4 && f.Message.Contains("New package dependency 'Acme.Internal'"));
    }

    [Fact]
    public void Offline_new_project_pulls_lookalikes_out_of_the_summary()
    {
        var findings = Pr.Run(new NewPackageRule(), Pr.File("tests/A.Tests/A.Tests.csproj", null, WithPackages("xunit", "Microsoft.NET.Test.Sdkk")));

        Assert.Equal(2, findings.Count);
        Assert.Contains(findings, f => f.Message.Contains("'Microsoft.NET.Test.Sdkk'") && f.Message.Contains("'Microsoft.NET.Test.Sdk'"));
        Assert.Contains(findings, f => f.Message.Contains("New project with 1 package(s): xunit"));
    }

    [Fact]
    public void Online_a_popular_package_is_not_called_a_typo()
    {
        // Pretend a lookalike name is itself a big, legitimate package.
        var registry = new FakeRegistry(new PackageInfo("Newtonsoft.Jsonn", true, 50_000_000));
        var finding = Assert.Single(Pr.Run(new NewPackageRule(registry), Pr.File("src/A/A.csproj", WithPackages(), WithPackages("Newtonsoft.Jsonn"))));
        Assert.Equal(Severity.Info, finding.Severity);
        Assert.DoesNotContain("typo", finding.Message);
    }

    [Theory]
    [InlineData(312, "312")]
    [InlineData(4_500, "4.5K")]
    [InlineData(12_000_000, "12M")]
    [InlineData(1_234_000_000, "1.2B")]
    public void Download_counts_are_short(long count, string expected) =>
        Assert.Equal(expected, NewPackageRule.Downloads(count));

    [Fact]
    public void WeakenedChecks_flags_settings_that_turn_checks_off()
    {
        var after = Project.Replace(
            "<TargetFramework>net10.0</TargetFramework>",
            "<TargetFramework>net10.0</TargetFramework>\n    <Nullable>disable</Nullable>\n    <NoWarn>CS1591;CS8618</NoWarn>");

        var findings = Pr.Run(new WeakenedChecksRule(), Pr.File("src/App/App.csproj", Project, after));
        Assert.Equal([4, 5], findings.Select(f => f.Line!.Value));
    }

    [Fact]
    public void WeakenedChecks_reads_editorconfig()
    {
        var file = Pr.File(".editorconfig", "[*.cs]\n", "[*.cs]\ndotnet_diagnostic.CA2007.severity = none\n");
        Assert.Single(Pr.Run(new WeakenedChecksRule(), file));
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml", true)]
    [InlineData("Directory.Build.props", true)]
    [InlineData("src/.editorconfig", true)]
    [InlineData("src/App/App.csproj", false)]
    [InlineData("src/App/Program.cs", false)]
    public void BuildConfigChanged_recognizes_build_files(string path, bool expected)
    {
        var findings = Pr.Run(new BuildConfigChangedRule(), Pr.File(path, "a\n", "b\n"));
        Assert.Equal(expected, findings.Count == 1);
    }

    [Theory]
    [InlineData("tests/Foo.cs", true)]
    [InlineData("src/App.Tests/Foo.cs", true)]
    [InlineData("src/App/CalculatorTests.cs", true)]
    [InlineData("src/App/latest.cs", false)]
    [InlineData("src/App/Contest.cs", false)]
    [InlineData("src/App/Calculator.cs", false)]
    public void Test_files_are_recognized_by_path(string path, bool expected) =>
        Assert.Equal(expected, FileClassifier.IsTestFile(path));
}
