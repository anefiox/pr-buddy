using System.Globalization;

namespace PrBuddy.Core.Reporting;

/// <summary>
/// Every number PR Buddy writes goes through here, in one fixed format. Formatting with the
/// machine's culture made reports differ between machines ("100%" in English, "100 %" in the
/// invariant culture, "1,2B" in German), which breaks "the same diff always gives the same report".
/// </summary>
public static class Numbers
{
    /// <summary>0.875 → "88%". Rounds halves away from zero, as "P0" did on English systems.</summary>
    public static string Percent(double share) =>
        ((int)Math.Round(share * 100, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>1.25 → "1.3", 12.0 → "12": at most one decimal, always a '.'.</summary>
    public static string OneDecimal(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
