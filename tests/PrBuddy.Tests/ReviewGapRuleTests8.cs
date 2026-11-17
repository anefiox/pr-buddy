using PrBuddy.Core.Configuration;
using PrBuddy.Core.Rules.Code;

namespace PrBuddy.Tests;

/// <summary>Eighth round of rules from comparing PR Buddy with human reviews of open PRs.</summary>
public sealed class ReviewGapRuleTests8
{
    static string Provider(string added) => $$"""
        public class AuthProvider : BaseProvider
        {
            public AuthProvider(Credential credential, string[]? hosts = null, params string[] scopes)
                : base(new TokenProvider(credential, hosts, scopes))
            {
            }
            {{added}}
        }
        """;

    [Fact]
    public void ReportsNewConstructorThatForksTheBaseConstruction()
    {
        var after = Provider("public AuthProvider(Credential credential, bool cae, params string[] scopes) : base(new TokenProvider(credential, cae, scopes)) { }");
        var finding = Assert.Single(Pr.Run(new ForkedConstructorRule(), Pr.File("src/AuthProvider.cs", Provider(""), after)));
        Assert.Contains("like the constructor at line 3", finding.Message);
    }

    [Theory]
    [InlineData("public AuthProvider(Credential credential, bool cae) : this(credential, null, []) { }")]
    [InlineData("public AuthProvider(Credential credential, Other other) : base(new OtherProvider(other)) { }")]
    [InlineData("public AuthProvider() : base(null) { }")]
    public void AllowsChainingAndDifferentBases(string added) =>
        Assert.Empty(Pr.Run(new ForkedConstructorRule(), Pr.File("src/AuthProvider.cs", Provider(""), Provider(added))));

    static string Options(string body) => $$"""
        public class TokenProvider
        {
            public TokenProvider(Credential credential, ProviderOptions? options, params string[] scopes)
            {
                {{body}}
            }
        }
        """;

    [Fact]
    public void ReportsParameterNullCheckedThreeTimes()
    {
        var finding = Assert.Single(Pr.Run(new RepeatedNullCheckRule(), Pr.File("src/TokenProvider.cs", null,
            Options("_hosts = new(options?.AllowedHosts); _source = new((options?.Observability ?? new()).Name); _cae = options?.IsCaeEnabled ?? true;"))));
        Assert.Contains("null-checked 3 times", finding.Message);
        Assert.Contains("options ??= new ProviderOptions();", finding.Message);
    }

    [Theory]
    [InlineData("options ??= new(); _hosts = new(options.AllowedHosts); _cae = options.IsCaeEnabled;")]
    [InlineData("_hosts = new(options?.AllowedHosts); _cae = options?.IsCaeEnabled ?? true;")]
    public void AllowsDefaultingOnceOrFewChecks(string body) =>
        Assert.Empty(Pr.Run(new RepeatedNullCheckRule(), Pr.File("src/TokenProvider.cs", null, Options(body))));

    [Fact]
    public void IgnoresNullableValueTypes() =>
        Assert.Empty(Pr.Run(new RepeatedNullCheckRule(), Pr.File("src/Flags.cs", null,
            "class Flags { void Set(bool? a) { x = a ?? false; y = a ?? true; z = a ?? false; } }")));

    const string Projection = """
        namespace App.DomainModel;
        public static class StatusHistoryProjection
        {
            public static object Build(object changes) => AccreditationApiMapper.BuildHistory(changes, 2026);
        }
        """;

    static IReadOnlyDictionary<string, BannedApi> Layering => new Dictionary<string, BannedApi>
    {
        ["AccreditationApiMapper"] = new("The domain model must not call the API layer", [new Glob("**/DomainModel/**")]),
    };

    [Fact]
    public void ScopedBanReportsUseInsideThoseFolders()
    {
        var finding = Assert.Single(Pr.Run(new BannedApiRule(Layering), Pr.File("src/DomainModel/StatusHistoryProjection.cs", null, Projection)));
        Assert.Contains("banned in this part of the code: The domain model must not call the API layer", finding.Message);
    }

    [Fact]
    public void ScopedBanAllowsUseElsewhere() =>
        Assert.Empty(Pr.Run(new BannedApiRule(Layering), Pr.File("src/AccreditationApi/Endpoints.cs", null, Projection)));

    [Fact]
    public void BannedApisAreReadFromConfigWithScopes()
    {
        var config = PrBuddyConfig.Parse("""
            { "rules": { "PRB034": { "apis": {
                ".Chunk": "No LINQ",
                "AccreditationApiMapper": { "reason": "Layering", "in": ["**/DomainModel/**"] } } } } }
            """, "prbuddy.json");
        var apis = config.GetBannedApis("PRB034", "apis");
        Assert.True(apis[".Chunk"].AppliesTo("src/Anything.cs"));
        Assert.True(apis["AccreditationApiMapper"].AppliesTo("src/DomainModel/Status/Projection.cs"));
        Assert.False(apis["AccreditationApiMapper"].AppliesTo("src/AccreditationApi/Mapper.cs"));
    }

    [Fact]
    public void BannedApiConfigRejectsABadScope() =>
        Assert.Throws<PrBuddy.Core.PrBuddyException>(() => PrBuddyConfig.Parse(
            """{ "rules": { "PRB034": { "apis": { "X": { "reason": "r", "in": "src" } } } } }""", "prbuddy.json")
            .GetBannedApis("PRB034", "apis"));
}
