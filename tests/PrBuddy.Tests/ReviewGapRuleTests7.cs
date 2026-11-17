using PrBuddy.Core;
using PrBuddy.Core.Packages;
using PrBuddy.Core.Rules.Code;
using PrBuddy.Core.Rules.Config;
using PrBuddy.Core.Rules.Structure;

namespace PrBuddy.Tests;

/// <summary>Seventh round: rules for misses that a deterministic check can catch, from the review comparison sheet.</summary>
public sealed class ReviewGapRuleTests7
{
    static string Commented(string comment) => $"class Coverage\n{{\n    {comment}\n    void Run() {{ }}\n}}";

    [Theory]
    [InlineData("// One frame of the former recursive algorithm.", "the former")]
    [InlineData("// Inputs, mirroring the parameters CoverInstruction used to take when it recursed.", "used to take")]
    [InlineData("/// <summary>Replacing the old code path, this loops instead.</summary>", "Replacing the old")]
    [InlineData("// This PR adds a cache here.", "This PR adds")]
    public void ReportsCommentsThatNarrateTheChange(string comment, string phrase) =>
        Assert.Contains(phrase, Assert.Single(Pr.Run(new HistoryCommentRule(), Pr.File("src/Coverage.cs", null, Commented(comment)))).Message);

    [Theory]
    [InlineData("// An object that previously matched the query has left it.")]
    [InlineData("// Names are no longer than 64 characters.")]
    [InlineData("// Counts the files the PR changes.")]
    [InlineData("// Walks the blocks iteratively so deep input can't overflow the stack.")]
    public void AllowsCommentsAboutTheCodeAsItIs(string comment) =>
        Assert.Empty(Pr.Run(new HistoryCommentRule(), Pr.File("src/Coverage.cs", null, Commented(comment))));

    [Fact]
    public void RegressionNotesInTestsAreFine() =>
        Assert.Empty(Pr.Run(new HistoryCommentRule(), Pr.File("tests/CoverageTests.cs", null, Commented("// This used to throw on an empty list."))));

    static readonly Dictionary<string, string> Banned = new()
    {
        ["Utf8Parser.TryParse"] = "Use SessionParseState.TryGetInt",
        [".Chunk"] = "No LINQ in production code",
    };

    [Theory]
    [InlineData("var ok = Utf8Parser.TryParse(slice, out int db, out _);", "Utf8Parser.TryParse")]
    [InlineData("var ok = System.Buffers.Text.Utf8Parser.TryParse(slice, out int db, out _);", "Utf8Parser.TryParse")]
    [InlineData("foreach (var window in hashes.Chunk(64)) { }", ".Chunk")]
    public void ReportsBannedApis(string statement, string api)
    {
        var finding = Assert.Single(Pr.Run(new BannedApiRule(Banned), Pr.File("src/Commands.cs", null, $"class Commands {{ void Run() {{ {statement} }} }}")));
        Assert.Contains(api, finding.Message);
        Assert.Contains("is banned in this repository", finding.Message);
    }

    [Fact]
    public void BannedApisInTestsNeedIncludeTests()
    {
        var test = Pr.File("tests/CommandsTests.cs", null, "class CommandsTests { void Run() { var c = hashes.Chunk(64); } }");
        Assert.Empty(Pr.Run(new BannedApiRule(Banned), test));
        Assert.Single(Pr.Run(new BannedApiRule(Banned, includeTests: true), test));
    }

    [Fact]
    public void NoBannedApisMeansNoFindings() =>
        Assert.Empty(Pr.Run(new BannedApiRule(new Dictionary<string, string>()),
            Pr.File("src/Commands.cs", null, "class Commands { void Run() { var c = hashes.Chunk(64); } }")));

    [Fact]
    public void BannedApiMapIsReadFromConfig()
    {
        var config = PrBuddy.Core.Configuration.PrBuddyConfig.Parse(
            """{ "rules": { "PRB034": { "apis": { ".Chunk": "No LINQ" }, "includeTests": true } } }""", "prbuddy.json");
        Assert.Equal("No LINQ", config.GetStringMap("PRB034", "apis")[".Chunk"]);
        Assert.True(config.GetBool("PRB034", "includeTests", false));
    }

    const string NullableFile = "// header\n\n#nullable enable\n\nclass Converter { }";

    [Theory]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"></Project>", "has them off")]
    [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>", "does nothing")]
    public void ReportsPerFileNullableEnable(string project, string expected) =>
        Assert.Contains(expected, Assert.Single(Pr.RunInRepo(new FileNullableEnableRule(),
            new Dictionary<string, string> { ["test/App.Tests.csproj"] = project },
            Pr.File("test/ConverterTest.cs", "class Converter { }", NullableFile))).Message);

    [Fact]
    public void SkipsProjectsThatEnableNullableFileByFile()
    {
        var repo = new Dictionary<string, string> { ["src/Lib.csproj"] = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>" };
        for (var i = 0; i < 10; i++) repo[$"src/Old{i}.cs"] = "#nullable enable\nclass Old { }";
        Assert.Empty(Pr.RunInRepo(new FileNullableEnableRule(), repo, Pr.File("src/Converter.cs", "class Converter { }", NullableFile)));
    }

    [Fact]
    public void ReportsStrayWhitespaceOncePerFile()
    {
        var after = "class Forms\n{\n    void A() { }\n  \n    void B() { }   \n}";
        var finding = Assert.Single(Pr.Run(new StrayWhitespaceRule(), Pr.File("src/Forms.cs", "class Forms\n{\n}", after)));
        Assert.Contains("2 added lines", finding.Message);
        Assert.Empty(Pr.Run(new StrayWhitespaceRule(), Pr.File("src/Forms.cs", null, "class Forms\n{\n\n}")));
    }

    static string Handler(string body) => $"class Handler {{ bool Unpaid(Event e, Sub s) => true; void Handle(Event e, Sub s) {{ {body} }} }}";

    [Fact]
    public void ReportsTheSameConditionComputedTwice()
    {
        var finding = Assert.Single(Pr.Run(new RepeatedConditionCallRule(), Pr.File("src/Handler.cs", null,
            Handler("var lookUp = s.Id != e.Id || Unpaid(e, s); if (Unpaid(e, s)) { Disable(); }"))));
        Assert.Contains("'Unpaid(e, s)'", finding.Message);
    }

    [Theory]
    [InlineData("switch (mode) { case 1: if (client.Exists(path)) Delete(); break; case 2: if (client.Exists(path)) Throw(); break; }")]
    [InlineData("if (ready) { if (Unpaid(e, s)) A(); } else { if (Unpaid(e, s)) B(); }")]
    [InlineData("if (Unpaid(e, s)) A(); s = Reload(); if (Unpaid(e, s)) B();")]
    [InlineData("Log(Unpaid(e, s)); Log(Unpaid(e, s));")]
    public void AllowsExclusiveBranchesReassignedArgumentsAndNonConditions(string body) =>
        Assert.Empty(Pr.Run(new RepeatedConditionCallRule(), Pr.File("src/Handler.cs", null, Handler(body))));

    static string Registration(string call) => $$"""
        using System.Threading;
        class Command
        {
            void Setup(CancellationToken token, Runner cmd) { {{call}} }
        }
        """;

    [Fact]
    public void ReportsRegisterLambdaThatCapturesOneVariable()
    {
        var finding = Assert.Single(Pr.Run(new CapturingRegistrationRule(), Pr.File("src/Command.cs", null, Registration("token.Register(() => cmd.Cancel());"))));
        Assert.Contains("captures 'cmd'", finding.Message);
    }

    [Theory]
    [InlineData("token.Register(static state => ((Runner)state!).Cancel(), cmd);")]
    [InlineData("token.Register(() => Stop());")]
    [InlineData("new Custom().Register(() => cmd.Cancel());")]
    public void AllowsStaticLambdasNoCaptureAndOtherReceivers(string call) =>
        Assert.Empty(Pr.Run(new CapturingRegistrationRule(), Pr.File("src/Command.cs", null, Registration(call))));

    static string Component(string code) => $$"""
        @page "/forms"
        <EditForm Model="model">
            <InputSelect @bind-Value="model.SelectedCities" />
        </EditForm>

        @code {
            Model model = new();

            class Model
            {
                public City[] SelectedCities { get; set; } = new[] { City.SanFrancisco };
                public string[] HostileStrings { get; set; } = new string[] { "\"", "{" };
        {{code}}
            }
        }
        """;

    [Fact]
    public void ReportsUnusedMemberInARazorComponent()
    {
        var after = Component("        public City[]? SelectedCitiesNullable { get; set; } = null;");
        var finding = Assert.Single(Pr.Run(new UnusedRazorMemberRule(), Pr.File("src/Forms.razor", Component(""), after)));
        Assert.Contains("'SelectedCitiesNullable'", finding.Message);
        Assert.Equal(13, finding.Line);
    }

    [Theory]
    [InlineData("        [Parameter] public int Count { get; set; }")]
    [InlineData("        protected override void OnInitialized() { }")]
    public void AllowsFrameworkMembersInRazor(string member) =>
        Assert.Empty(Pr.Run(new UnusedRazorMemberRule(), Pr.File("src/Forms.razor", Component(""), Component(member))));

    [Fact]
    public void RazorMembersUsedInOtherFilesAreFine() =>
        Assert.Empty(Pr.Run(new UnusedRazorMemberRule(),
            Pr.File("src/Forms.razor", Component(""), Component("        public int Total { get; set; }")),
            Pr.File("src/FormsTests.cs", null, "class FormsTests { int T(Forms f) => f.Total; }")));

    [Fact]
    public void EncodingCheckLooksAtMovedCodeInRenamedFiles()
    {
        const string code = "using System.Text; class Header { bool Parse(string header) { var bytes = Encoding.UTF8.GetBytes(header); if (bytes.Length > 16384) return false; return true; } }";
        var same = Pr.File("src/Context/Header.cs", code, code);
        var moved = new ChangedFile
        {
            Path = same.Path, OldPath = "src/Common/CallerHeader.cs", Status = ChangeStatus.Renamed,
            OldContent = code, NewContent = code, AddedLines = same.AddedLines, DeletedLineCount = 0,
        };
        Assert.Single(Pr.Run(new EncodeBeforeLengthCheckRule(), moved));
    }

    sealed class Registry(params PackageInfo[] packages) : IPackageRegistry
    {
        public PackageInfo? Lookup(string id) => packages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReportsDeprecatedToolsInTheManifest()
    {
        const string before = """{ "version": 1, "tools": { "dotnet-format": { "version": "5.1.0" } } }""";
        const string after = """
            { "version": 1, "tools": {
                "dotnet-format": { "version": "5.1.0" },
                "microsoft.playwright.cli": { "version": "1.2.3", "commands": [ "playwright" ] } } }
            """;
        var registry = new Registry(
            new PackageInfo("dotnet-format", true, 1_000_000),
            new PackageInfo("Microsoft.Playwright.CLI", true, 3_400_000, true, Deprecation: "Legacy; use playwright.ps1 instead"));
        var finding = Assert.Single(Pr.Run(new NewPackageRule(registry), Pr.File(".config/dotnet-tools.json", before, after)));
        Assert.Contains("deprecated by its owner (Legacy", finding.Message);
        Assert.Empty(Pr.Run(new NewPackageRule(), Pr.File(".config/dotnet-tools.json", before, after)));
    }

    [Fact]
    public void ReportsDeprecatedAndVulnerablePackageReferences()
    {
        const string project = "<Project><ItemGroup>\n<PackageReference Include=\"Old.Lib\" Version=\"1.0.0\" />\n<PackageReference Include=\"Leaky.Lib\" Version=\"2.0.0\" />\n</ItemGroup></Project>";
        var registry = new Registry(
            new PackageInfo("Old.Lib", true, 5_000_000, Deprecation: "Legacy; use New.Lib instead"),
            new PackageInfo("Leaky.Lib", true, 9_000_000, Vulnerabilities: 2));
        var findings = Pr.Run(new NewPackageRule(registry), Pr.File("src/App/App.csproj", "<Project></Project>", project));
        Assert.Contains(findings, f => f.Message.Contains("deprecated", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Message.Contains("2 known vulnerabilities", StringComparison.Ordinal));
    }
}
