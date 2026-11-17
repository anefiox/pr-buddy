// prbuddy-ignore-file PRB108: the real hosts here are the rule's own test data and are never contacted
using PrBuddy.Core;
using PrBuddy.Core.Rules.Code;
using PrBuddy.Core.Rules.Config;
using PrBuddy.Core.Rules.Testing;

namespace PrBuddy.Tests;

/// <summary>Fifth round of rules and fixes from comparing PR Buddy with human reviews of open PRs.</summary>
public sealed class ReviewGapRuleTests5
{
    [Theory]
    [InlineData("dev/db/bitwarden.sqlite", "database")]
    [InlineData("src/App/bin/Debug/App.pdb", "bin folder")]
    [InlineData("src/App/App.csproj.user", "IDE")]
    [InlineData("logs/server.log", "log")]
    public void ReportsCommittedLocalArtifacts(string path, string expected) =>
        Assert.Contains(expected, Assert.Single(Pr.Run(new CommittedArtifactRule(), Pr.File(path, null, "x"))).Message);

    [Theory]
    [InlineData("tests/App.Tests/testdata/sample.sqlite")]
    [InlineData("samples/Northwind.db")]
    [InlineData("src/App/Program.cs")]
    public void AllowsTestDataSamplesAndSource(string path) =>
        Assert.Empty(Pr.Run(new CommittedArtifactRule(), Pr.File(path, null, "x")));

    [Fact]
    public void OnlyAddedArtifactsCount() =>
        Assert.Empty(Pr.Run(new CommittedArtifactRule(), Pr.File("dev/db/app.sqlite", "a", "b")));

    [Fact]
    public void ReportsTupleItemAccessOncePerMethod()
    {
        const string code = """
            class Handler
            {
                (Guid? Org, Guid? User) Ids() => default;
                void Run() { var a = GetIds().Item2; var b = GetIds().Item1; }
            }
            """;
        var finding = Assert.Single(Pr.Run(new TupleItemAccessRule(), Pr.File("src/Handler.cs", null, code)));
        Assert.Contains("Item1, Item2", finding.Message);
    }

    [Fact]
    public void AllowsDeconstructionAndNamedElements() =>
        Assert.Empty(Pr.Run(new TupleItemAccessRule(), Pr.File("src/Handler.cs", null,
            "class Handler { void Run() { var (org, user, _) = GetIds(); var u = GetIds().UserId; var x = Items.Item10; } }")));

    [Theory]
    [InlineData("public const string ClientSecret = \"vqTzrsvdhYmunjzXeynn2zu2VtA4z0RdUl1OQ9c6\";", Severity.Warning)]
    [InlineData("public const string ClientId = \"vqTzrsvdhYmunjzXeynn2zu2VtA4z0RdUl1OQ9c6\";", Severity.Info)]
    [InlineData("void M() { options.ApiKey = \"Zk29fQpL7xWm4RtYb83NcVe6HsJa1Dg5\"; }", Severity.Warning)]
    public void ReportsRandomLookingCredentialValues(string member, Severity severity)
    {
        var finding = Assert.Single(Pr.Run(new HardcodedCredentialValueRule(), Pr.File("src/Import.cs", null, $"class Import {{ {member} }}")));
        Assert.Equal(severity, finding.Severity);
        Assert.DoesNotContain("vqTzrsvd", finding.Message);
        Assert.DoesNotContain("Zk29fQpL", finding.Message);
    }

    [Theory]
    [InlineData("public const string ClientSecret = \"\";")]
    [InlineData("public const string ApiKey = \"your-api-key-goes-here-please\";")]
    [InlineData("public const string ClientId = \"https://login.example.com/app\";")]
    [InlineData("public const string DisplayName = \"vqTzrsvdhYmunjzXeynn2zu2VtA4z0RdUl1OQ9c6\";")]
    public void AllowsPlaceholdersUrlsAndOtherNames(string member) =>
        Assert.Empty(Pr.Run(new HardcodedCredentialValueRule(), Pr.File("src/Import.cs", null, $"class Import {{ {member} }}")));

    [Fact]
    public void CredentialValuesInTestsAreLeftToTheTests() =>
        Assert.Empty(Pr.Run(new HardcodedCredentialValueRule(), Pr.File("tests/ImportTests.cs", null,
            "class ImportTests { const string ApiKey = \"Zk29fQpL7xWm4RtYb83NcVe6HsJa1Dg5\"; }")));

    [Fact]
    public void EfCoreMigrationsCountAsGenerated()
    {
        Assert.True(FileClassifier.IsGenerated("util/SqliteMigrations/Migrations/20261111175748_AddExternalName.cs", "#nullable disable"));
        Assert.True(FileClassifier.IsGenerated("src/Data/Migrations/AppDbContextModelSnapshot.cs", ""));
        Assert.False(FileClassifier.IsGenerated("src/Data/Migrations/MigrationRunner.cs", ""));
        Assert.Empty(Pr.Run(new SuppressedWarningsRule(),
            Pr.File("util/SqliteMigrations/Migrations/20261111175748_AddExternalName.cs", null, "#nullable disable\nclass AddExternalName { }")));
    }

    [Fact]
    public void HostsTheProductionCodeCallsAreFineInItsTests()
    {
        const string production = "class MdbList { const string Api = \"https://api.mdblist.com/lists\"; }";
        const string test = "public class MdbListTests { [Fact] public void Url() => Assert.Equal(\"https://www.mdblist.com/lists/x\", Build()); }";
        Assert.Empty(Pr.Run(new ExternalHostInTestRule(),
            Pr.File("src/MdbList.cs", null, production), Pr.File("tests/MdbListTests.cs", null, test)));
        Assert.Single(Pr.Run(new ExternalHostInTestRule(), Pr.File("tests/MdbListTests.cs", null, test)));
    }
}
