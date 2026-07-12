using System.Globalization;

namespace KittyClaw.Web.Services;

/// <summary>Compact, culture-invariant formatting for agent token counts and USD costs.</summary>
public static class TokenDisplay
{
    /// <summary>1 234 → "1.2k", 5 300 000 → "5.3M". Invariant culture (badge text, not prose).</summary>
    public static string Compact(long tokens) => tokens switch
    {
        >= 1_000_000 => (tokens / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (tokens / 1_000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => tokens.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>"$1.23", with extra precision under a cent so small runs don't all show "$0.00".</summary>
    public static string Usd(double usd) =>
        "$" + usd.ToString(usd > 0 && usd < 0.01 ? "0.0000" : "0.00", CultureInfo.InvariantCulture);
}
