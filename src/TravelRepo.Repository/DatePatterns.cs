using System.Globalization;

namespace TravelRepo.Repository;

/// <summary>Date patterns for exported text, which culture data alone does not get right for German.</summary>
internal static class DatePatterns
{
    /// <summary>
    /// A pattern written as "ddd d MMM" the way the culture's language writes it. German puts a full stop after the day
    /// of the month and a comma after a short weekday: "Fr, 4. Jun". Other languages keep the pattern.
    /// </summary>
    public static string For(string pattern, CultureInfo culture) => culture.TwoLetterISOLanguageName == "de"
        ? pattern.Replace("ddd d", "ddd, d", StringComparison.Ordinal).Replace("d MMM", "d. MMM", StringComparison.Ordinal)
        : pattern;
}
