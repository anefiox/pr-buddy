using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Structure;

/// <summary>
/// PRB024: a new or renamed file with one top-level type whose name doesn't match the file name.
/// "Go to file" and search find the wrong thing, and a case-only difference breaks on case-sensitive
/// file systems that tooling assumes match.
/// </summary>
public sealed class FileNameMismatchRule : RuleBase
{
    public override RuleDescriptor Descriptor { get; } = new("PRB024", "File name doesn't match its type", Severity.Info, "Structure");

    public override IEnumerable<Finding> Analyze(PullRequestContext context)
    {
        foreach (var file in context.CSharpFiles)
        {
            if (file.NewTree is null || file.IsGenerated || file.File.Status is not (ChangeStatus.Added or ChangeStatus.Renamed))
                continue;

            var types = file.NewTree.GetRoot().DescendantNodes(n => n is not BaseTypeDeclarationSyntax)
                .OfType<MemberDeclarationSyntax>().Where(m => m is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax).ToArray();
            if (types.Length != 1) continue;

            var typeName = types[0] switch
            {
                BaseTypeDeclarationSyntax t => t.Identifier.ValueText,
                DelegateDeclarationSyntax d => d.Identifier.ValueText,
                _ => null,
            };
            if (typeName is null || types[0].Modifiers.Any(m => m.ValueText == "partial")) continue;

            // "Widget.Designer.cs", "Widget`1.cs", "Widget{T}.cs" and "WidgetOfT.cs" all name Widget.
            var fileName = System.IO.Path.GetFileNameWithoutExtension(file.Path).Split('.')[0].Split('`', '{')[0];
            if (fileName == typeName || fileName == typeName + "OfT") continue;

            var caseOnly = string.Equals(fileName, typeName, StringComparison.OrdinalIgnoreCase);
            yield return Report(file.Path, AnalyzedFile.LineOf(types[0]), caseOnly
                ? $"File '{fileName}.cs' holds '{typeName}', which differs only in case. Rename the file (or the type) so they match exactly."
                : $"File '{fileName}.cs' holds '{typeName}'. Rename one so the file can be found from the type.");
        }
    }
}
