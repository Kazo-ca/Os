using System.Globalization;

namespace Kazo.Os.AppSdk.Craft;

/// <summary>
/// The language the Craft app shows: the
/// Kazo user's language when the Craft app has it, English otherwise.
/// </summary>
public static class CraftLanguage
{
    /// <summary>The Craft app's fallback, always available.</summary>
    public const string English = "en";

    /// <summary>
    /// Picks the Craft app's language code for a culture: <c>zh-hant</c> for traditional Chinese (script
    /// Hant, or Taiwan, Hong Kong, Macao), <c>zh-hans</c> for the other Chinese, <c>pt-br</c> for any
    /// Portuguese, <c>sr-latn</c> for Serbian in Latin script, else the two-letter code. When the app
    /// does not offer that code but offers the two-letter one (an app that names its languages by
    /// two letters, as GridCraft does), the two-letter code.
    /// </summary>
    /// <param name="culture">The Kazo user's culture.</param>
    /// <param name="available">The codes the Craft app offers (WordCraft's <c>ui.language</c> lists them; another app gives its own list).</param>
    /// <returns>A code of <paramref name="available"/>, or <see cref="English"/>.</returns>
    public static string Pick(CultureInfo culture, IReadOnlyCollection<string> available)
    {
        ArgumentNullException.ThrowIfNull(culture);
        ArgumentNullException.ThrowIfNull(available);
        string name = culture.Name.ToLowerInvariant();
        string code = culture.TwoLetterISOLanguageName switch
        {
            "zh" when name.Contains("-hant", StringComparison.Ordinal)
                || name.EndsWith("-tw", StringComparison.Ordinal)
                || name.EndsWith("-hk", StringComparison.Ordinal)
                || name.EndsWith("-mo", StringComparison.Ordinal) => "zh-hant",
            "zh" => "zh-hans",
            "pt" => "pt-br",
            "sr" when name.Contains("-latn", StringComparison.Ordinal) => "sr-latn",
            string twoLetters => twoLetters,
        };
        if (available.Contains(code, StringComparer.OrdinalIgnoreCase))
        {
            return code;
        }

        // An app that names its languages by two letters only (GridCraft: pt, sr) gets them.
        string twoLetter = culture.TwoLetterISOLanguageName.ToLowerInvariant();
        return available.Contains(twoLetter, StringComparer.OrdinalIgnoreCase) ? twoLetter : English;
    }
}
