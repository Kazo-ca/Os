using Kazo.Os.AppSdk.Apps;

namespace Kazo.Os.Apps.FileExplorer;

/// <summary>
/// The search box and the category selector of the "Open with" box: which installed apps stay
/// listed for a search text and a category. Kept apart from the markup so the rules are tested
/// without a browser.
/// </summary>
internal static class OpenWithAppFilter
{
    /// <summary>
    /// The categories of the listed apps, each once, in the order the selector shows them: by their
    /// shown label, then by id. An app without a category adds none.
    /// </summary>
    /// <param name="apps">The apps the box lists before any filter.</param>
    /// <param name="label">Gives a category's shown label (translated, or title-cased).</param>
    internal static IReadOnlyList<string> Categories(IEnumerable<KazoAppInfo> apps, Func<string, string> label) =>
        [.. apps
            .Select(app => app.Category)
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(label, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(category => category, StringComparer.Ordinal)];

    /// <summary>
    /// The apps that match: in <paramref name="category"/> when one is chosen, and whose name, id or
    /// description contains every word of <paramref name="search"/>, ignoring case. The order of
    /// <paramref name="apps"/> is kept (the OS's candidates keep theirs).
    /// </summary>
    /// <param name="apps">The apps the box lists before any filter.</param>
    /// <param name="search">The text typed in the search box; blank keeps every app.</param>
    /// <param name="category">The chosen category id; <see langword="null"/> or blank keeps every category.</param>
    internal static IReadOnlyList<KazoAppInfo> Apply(IEnumerable<KazoAppInfo> apps, string? search, string? category)
    {
        string[] words = string.IsNullOrWhiteSpace(search)
            ? []
            : search.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. apps.Where(app =>
            (string.IsNullOrWhiteSpace(category) || string.Equals(app.Category, category, StringComparison.OrdinalIgnoreCase))
            && words.All(word => Contains(app.Name, word) || Contains(app.Id, word) || Contains(app.Description, word)))];
    }

    private static bool Contains(string? text, string word) =>
        text is not null && text.Contains(word, StringComparison.CurrentCultureIgnoreCase);
}
