using System.Numerics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PrBuddy.Core.Rules.Code;

/// <summary>
/// PRB020: a new member of a [Flags] enum whose value is a plain number (or an implicit one) that
/// isn't a single bit. It overlaps other flags, so setting it also sets them. Combinations written
/// with member names, zero, -1 and an "all flags" mask are allowed.
/// </summary>
public sealed class FlagsEnumValueRule : AddedCodeRule
{
    public override RuleDescriptor Descriptor { get; } =
        new("PRB020", "[Flags] enum value that isn't a single bit", Severity.Warning, "Code");

    protected override IEnumerable<Finding> AnalyzeFile(AnalyzedFile file, SyntaxNode root)
    {
        var enums = root.DescendantNodes().OfType<EnumDeclarationSyntax>()
            .Where(e => IsFlags(e) && e.Members.Any(file.IsNew)).ToArray();
        if (enums.Length == 0) yield break;

        var model = LocalSemantics.Create(root.SyntaxTree);
        foreach (var declaration in enums)
        {
            var values = declaration.Members
                .Select(m => (Member: m, Value: model.GetDeclaredSymbol(m) is IFieldSymbol { HasConstantValue: true } f
                    ? Bits(f.ConstantValue) : (ulong?)null))
                .Where(m => m.Value is not null).Select(m => (m.Member, Value: m.Value!.Value)).ToArray();
            var used = values.Aggregate(0UL, (all, m) => all | m.Value);

            foreach (var (member, value) in values)
            {
                if (!file.IsNew(member) || value == 0 || BitOperations.IsPow2(value) || value == ulong.MaxValue ||
                    member.EqualsValue?.Value.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any() == true)
                    continue;

                var others = values.Where(m => m.Member != member).ToArray();
                // An explicit "All" mask; an implicit value that happens to equal it is still a counting mistake.
                if (member.EqualsValue is not null && value == others.Aggregate(0UL, (all, m) => all | m.Value)) continue;

                var overlaps = others.Where(m => m.Value != 0 && BitOperations.IsPow2(m.Value) && (m.Value & value) != 0)
                    .Select(m => m.Member.Identifier.ValueText).ToArray();
                var free = 1UL;
                while (free != 0 && (used & free) != 0) free <<= 1;

                var shown = member.EqualsValue is null ? $"gets the implicit value {value}" : $"has the value {value}";
                var overlap = overlaps.Length > 0 ? $" It shares bits with {string.Join(" | ", overlaps)}, so setting it sets those too." : "";
                var next = free != 0 ? $"Use the next free bit ({free}, or 1 << {BitOperations.TrailingZeroCount(free)}), " : "Use a free bit, ";
                yield return Report(file, member,
                    $"'{member.Identifier.ValueText}' in [Flags] enum '{declaration.Identifier.ValueText}' {shown}, which isn't a single bit.{overlap} " +
                    next + "or write it with member names if it's meant to be a combination.");
            }
        }
    }

    static bool IsFlags(EnumDeclarationSyntax declaration) =>
        declaration.AttributeLists.SelectMany(l => l.Attributes).Any(a =>
            (a.Name is QualifiedNameSyntax q ? q.Right.Identifier.ValueText : a.Name.ToString()) is "Flags" or "FlagsAttribute");

    static ulong? Bits(object? constant) => constant switch
    {
        null => null,
        ulong u => u,
        _ => unchecked((ulong)Convert.ToInt64(constant)),
    };
}
