using System.Text.RegularExpressions;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB032: a new EF Core migration that EF can't discover. EF finds migrations through the
/// [DbContext] and [Migration] attributes, which dotnet ef writes into the .Designer.cs next to the
/// migration. A hand-written or half-copied migration without them is never applied, and nothing fails.
/// </summary>
public sealed partial class UndiscoverableMigrationRule : RuleBase
{
    public override RuleDescriptor Descriptor { get; } = new("PRB032", "Migration EF can't discover", Severity.Warning, "Code");

    public override IEnumerable<Finding> Analyze(PullRequestContext context)
    {
        var paths = context.Files.Where(f => f.File.NewContent is not null).Select(f => f.Path)
            .Concat(context.Snapshot.CSharpPaths).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in context.Files.Where(f => f.File.Status == ChangeStatus.Added && f.File.NewContent is not null))
        {
            var name = System.IO.Path.GetFileName(file.Path);
            if (!MigrationFile().IsMatch(name) || !file.Path.Replace('\\', '/').Contains("Migrations/", StringComparison.OrdinalIgnoreCase))
                continue;
            var content = file.File.NewContent!;
            if (!content.Contains(": Migration", StringComparison.Ordinal) || content.Contains("[Migration(", StringComparison.Ordinal))
                continue;

            var designer = file.Path[..^3] + ".Designer.cs";
            if (paths.Contains(designer)) continue;

            yield return Report(file.Path, null,
                $"Migration '{name}' has no {System.IO.Path.GetFileName(designer)} and no [Migration] attribute, so EF never discovers " +
                "or applies it, and nothing fails. Regenerate it with dotnet ef migrations add, which writes the designer file " +
                "with [DbContext(...)] and [Migration(\"...\")].");
        }
    }

    // 20261117122654_RemoveSensitiveData.cs, but not its .Designer.cs.
    [GeneratedRegex(@"^\d{14}_[^.]+\.cs$")]
    private static partial Regex MigrationFile();
}
