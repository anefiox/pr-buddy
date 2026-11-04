namespace PrBuddy.Core.Configuration;

/// <summary>The starter prbuddy.json written by `prbuddy init`. Everything is commented out, so it changes nothing until edited.</summary>
public static class ConfigTemplate
{
    public const string Text = """
        // PR Buddy settings. Comments and trailing commas are allowed.
        //
        // For pull requests this file is read from the BASE branch, so changes here only apply once
        // they're merged; a PR can't switch off its own checks. To try edits locally first:
        //   prbuddy --config prbuddy.json
        //
        // Rule details and options: prbuddy --list-rules, prbuddy --explain <rule>
        {
          // Files to leave out entirely (gitignore-style: * within a folder, ** across folders).
          "ignore": [
            // "**/Migrations/**",
            // "*.Designer.cs",
            // "docs/",
          ],

          // Exit with code 1 when any finding is at least this severity: info, warning or critical.
          // --fail-on on the command line overrides it.
          // "failOn": "warning",

          "rules": {
            // Turn a rule off:
            // "PRB008": "off",

            // Change a rule's severity (info, warning or critical):
            // "PRB004": "info",

            // Rule options:
            // "PRB006": { "threshold": 5 },                 // null-forgiving operators before it's reported
            // "PRB302": { "similarity": 0.85, "copySimilarity": 0.95, "minTokens": 40 },
            // "PRB303": { "focusShare": 0.7 },
            // "PRB013": { "threshold": 25 },                // method complexity before it's reported
          },
        }
        """;
}
