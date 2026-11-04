using System.Text.Json;

namespace PrBuddy.Core.Configuration;

/// <summary>Per-rule settings from prbuddy.json.</summary>
public sealed class RuleSettings
{
    public static RuleSettings Default { get; } = new();

    public bool Enabled { get; init; } = true;

    /// <summary>Replaces the severity of every finding the rule reports.</summary>
    public Severity? Severity { get; init; }

    /// <summary>Rule-specific options, such as PRB006's "threshold".</summary>
    public IReadOnlyDictionary<string, JsonElement> Options { get; init; } = new Dictionary<string, JsonElement>();
}

/// <summary>
/// Contents of prbuddy.json:
/// <code>
/// {
///   "ignore": ["**/Migrations/**", "docs/**"],
///   "failOn": "warning",
///   "rules": {
///     "PRB008": "off",
///     "PRB004": "info",
///     "PRB006": { "threshold": 5 }
///   }
/// }
/// </code>
/// </summary>
public sealed class PrBuddyConfig
{
    public const string FileName = "prbuddy.json";

    static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    readonly IReadOnlyList<Glob> _ignore;

    PrBuddyConfig(string source, IReadOnlyList<string> ignore, IReadOnlyDictionary<string, RuleSettings> rules, Severity? failOn)
    {
        Source = source;
        Ignore = ignore;
        Rules = rules;
        FailOn = failOn;
        _ignore = ignore.Select(p => new Glob(p)).ToList();
    }

    public static PrBuddyConfig Default { get; } =
        new(FileName, [], new Dictionary<string, RuleSettings>(StringComparer.OrdinalIgnoreCase), null);

    /// <summary>Where the config came from, for error messages.</summary>
    public string Source { get; }

    /// <summary>Glob patterns for files to leave out of the report entirely.</summary>
    public IReadOnlyList<string> Ignore { get; }

    public IReadOnlyDictionary<string, RuleSettings> Rules { get; }

    /// <summary>Used when --fail-on isn't passed on the command line.</summary>
    public Severity? FailOn { get; }

    public RuleSettings For(string ruleId) => Rules.TryGetValue(ruleId, out var settings) ? settings : RuleSettings.Default;

    public bool IsIgnored(string path) => _ignore.Any(g => g.IsMatch(path));

    public int GetInt(string ruleId, string option, int fallback)
    {
        if (!For(ruleId).Options.TryGetValue(option, out var value)) return fallback;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0
            ? number
            : throw new PrBuddyException($"{Source}: {ruleId}.{option} must be a positive whole number.");
    }

    /// <summary>Catches typos like "PRB01" before they silently do nothing.</summary>
    public void Validate(IEnumerable<string> knownRuleIds)
    {
        var known = knownRuleIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = Rules.Keys.Where(k => !known.Contains(k)).ToList();
        if (unknown.Count > 0)
            throw new PrBuddyException($"{Source} mentions unknown rule(s): {string.Join(", ", unknown)}. Run prbuddy --list-rules.");
    }

    public static PrBuddyConfig Parse(string json, string source)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new PrBuddyException($"{source} isn't valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new PrBuddyException($"{source} must contain a JSON object.");

            var ignore = new List<string>();
            var rules = new Dictionary<string, RuleSettings>(StringComparer.OrdinalIgnoreCase);
            Severity? failOn = null;

            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "$schema":
                        break;
                    case "ignore":
                        if (property.Value.ValueKind != JsonValueKind.Array || property.Value.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.String))
                            throw new PrBuddyException($"{source}: \"ignore\" must be an array of glob patterns.");
                        ignore.AddRange(property.Value.EnumerateArray().Select(e => e.GetString()!));
                        break;
                    case "failOn":
                        failOn = ParseSeverity(property.Value, $"{source}: \"failOn\"");
                        break;
                    case "rules":
                        if (property.Value.ValueKind != JsonValueKind.Object)
                            throw new PrBuddyException($"{source}: \"rules\" must be an object keyed by rule id.");
                        foreach (var rule in property.Value.EnumerateObject())
                            rules[rule.Name] = ParseRule(rule.Value, $"{source}: rule {rule.Name}");
                        break;
                    default:
                        throw new PrBuddyException($"{source}: unknown setting \"{property.Name}\". Expected ignore, failOn or rules.");
                }
            }

            return new PrBuddyConfig(source, ignore, rules, failOn);
        }
    }

    /// <summary>A rule is either a string ("off", "on", "info", "warning", "critical") or an object.</summary>
    static RuleSettings ParseRule(JsonElement value, string where)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString()!.ToLowerInvariant() switch
            {
                "off" => new RuleSettings { Enabled = false },
                "on" => RuleSettings.Default,
                _ => new RuleSettings { Severity = ParseSeverity(value, where) },
            };
        }

        if (value.ValueKind != JsonValueKind.Object)
            throw new PrBuddyException($"{where} must be \"off\", a severity, or an object.");

        var enabled = true;
        Severity? severity = null;
        var options = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name)
            {
                case "enabled":
                    enabled = property.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => throw new PrBuddyException($"{where}: \"enabled\" must be true or false."),
                    };
                    break;
                case "severity":
                    severity = ParseSeverity(property.Value, $"{where}: \"severity\"");
                    break;
                default:
                    options[property.Name] = property.Value.Clone();
                    break;
            }
        }
        return new RuleSettings { Enabled = enabled, Severity = severity, Options = options };
    }

    static Severity ParseSeverity(JsonElement value, string where)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        return text?.ToLowerInvariant() switch
        {
            "info" => Severity.Info,
            "warning" => Severity.Warning,
            "critical" => Severity.Critical,
            _ => throw new PrBuddyException($"{where} must be info, warning or critical."),
        };
    }
}
