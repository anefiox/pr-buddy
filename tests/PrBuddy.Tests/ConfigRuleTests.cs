using PrBuddy.Core;
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
