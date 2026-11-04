using System.Text;
using System.Text.RegularExpressions;

namespace PrBuddy.Core.Configuration;

/// <summary>
/// gitignore-style path patterns: * matches within one folder, ** across folders, ? one character.
/// A pattern with no slash matches at any depth, so "*.Designer.cs" means every Designer file.
/// </summary>
public sealed class Glob
{
    readonly Regex _regex;

    public Glob(string pattern)
    {
        Pattern = pattern;
        _regex = new Regex(ToRegex(pattern), RegexOptions.CultureInvariant);
    }

    public string Pattern { get; }

    public bool IsMatch(string path) => _regex.IsMatch(path.Replace('\\', '/'));

    static string ToRegex(string pattern)
    {
        var glob = pattern.Replace('\\', '/').TrimStart('/');
        if (glob.EndsWith('/')) glob += "**"; // "docs/" means everything under docs

        var regex = new StringBuilder("^");
        if (!glob.Contains('/')) regex.Append("(?:.*/)?");

        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                var slashFollows = i + 2 < glob.Length && glob[i + 2] == '/';
                regex.Append(slashFollows ? "(?:.*/)?" : ".*");
                i += slashFollows ? 2 : 1;
            }
            else if (c == '*') regex.Append("[^/]*");
            else if (c == '?') regex.Append("[^/]");
            else regex.Append(Regex.Escape(c.ToString()));
        }

        return regex.Append('$').ToString();
    }
}
