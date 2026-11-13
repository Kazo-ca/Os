using System.Text.Json;

namespace Kazo.Os.AppSdk.Craft;

/// <summary>One document a Craft app has open, as its <c>ui.inspect</c> lists it.</summary>
/// <param name="Id">The app's id of the document, for <c>engine.execute</c>'s <c>doc</c>.</param>
/// <param name="Name">
/// The file name its next save carries: the app's own (<c>path</c> in WordCraft's web build), else
/// the one the window derives from its title (<see cref="ListFrom(JsonElement, Func{string, string?})"/>);
/// <see langword="null"/> when unknown.
/// </param>
/// <param name="Title">Its title as the app shows it.</param>
/// <param name="Dirty">The app says it has unsaved changes.</param>
/// <param name="Active">It is the document the app shows.</param>
public sealed record OpenDocument(long Id, string? Name, string Title, bool Dirty, bool Active)
{
    /// <summary>Reads the documents of a <c>ui.inspect</c> answer.</summary>
    /// <param name="inspect">The app's answer.</param>
    /// <returns>The documents; empty when the answer has none, or is not a known shape.</returns>
    public static IReadOnlyList<OpenDocument> ListFrom(JsonElement inspect) => ListFrom(inspect, null);

    /// <summary>
    /// Reads the documents of a <c>ui.inspect</c> answer, in either shape the Craft apps give:
    /// <list type="bullet">
    /// <item>WordCraft's: <c>documents[{ id, path, title, dirty, active }]</c>;</item>
    /// <item>
    /// GridCraft's: <c>documents[{ title, dirty }]</c> without ids, and
    /// <c>windows[{ doc, index, active }]</c> where <c>doc</c> is the document's id and <c>index</c>
    /// its place in <c>documents</c>. A document shown in several windows is listed once, active
    /// when one of them is.
    /// </item>
    /// </list>
    /// The answer comes from the iframe: anything malformed is skipped.
    /// </summary>
    /// <param name="inspect">The app's answer.</param>
    /// <param name="nameOfTitle">
    /// Gives the file name of a document that has no <c>path</c>, from its title (GridCraft names its
    /// next save <c>&lt;title&gt;.xlsx</c>); <see langword="null"/> leaves such a name unknown.
    /// </param>
    /// <returns>The documents, in the app's order; empty when the answer is not a known shape.</returns>
    public static IReadOnlyList<OpenDocument> ListFrom(JsonElement inspect, Func<string, string?>? nameOfTitle)
    {
        if (inspect.ValueKind != JsonValueKind.Object
            || !inspect.TryGetProperty("documents", out JsonElement documents)
            || documents.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        bool hasIds = documents.EnumerateArray().Any(document => document.ValueKind == JsonValueKind.Object && document.TryGetProperty("id", out _));
        return !hasIds && inspect.TryGetProperty("windows", out JsonElement windows) && windows.ValueKind == JsonValueKind.Array
            ? ListByWindows(documents, windows, nameOfTitle)
            : ListByIds(documents, nameOfTitle);
    }

    private static List<OpenDocument> ListByIds(JsonElement documents, Func<string, string?>? nameOfTitle)
    {
        List<OpenDocument> list = [];
        foreach (JsonElement document in documents.EnumerateArray())
        {
            if (document.ValueKind != JsonValueKind.Object || !TryGetLong(document, "id", out long id))
            {
                continue;
            }

            string title = Text(document, "title") ?? string.Empty;
            list.Add(new OpenDocument(id, Text(document, "path") ?? NameOf(title, nameOfTitle), title, Flag(document, "dirty"), Flag(document, "active")));
        }

        return list;
    }

    private static List<OpenDocument> ListByWindows(JsonElement documents, JsonElement windows, Func<string, string?>? nameOfTitle)
    {
        List<OpenDocument> list = [];
        int count = documents.GetArrayLength();
        foreach (JsonElement window in windows.EnumerateArray())
        {
            if (window.ValueKind != JsonValueKind.Object
                || !TryGetLong(window, "doc", out long id)
                || !TryGetLong(window, "index", out long index)
                || index < 0 || index >= count)
            {
                continue;
            }

            bool active = Flag(window, "active");
            int listed = list.FindIndex(document => document.Id == id);
            if (listed >= 0)
            {
                // Another window of a document already listed: it only says whether it is active.
                list[listed] = list[listed] with { Active = list[listed].Active || active };
                continue;
            }

            JsonElement document = documents[(int)index];
            bool isObject = document.ValueKind == JsonValueKind.Object;
            string title = (isObject ? Text(document, "title") : null) ?? string.Empty;
            string? path = isObject ? Text(document, "path") : null;
            list.Add(new OpenDocument(id, path ?? NameOf(title, nameOfTitle), title, isObject && Flag(document, "dirty"), active));
        }

        return list;
    }

    private static string? NameOf(string title, Func<string, string?>? nameOfTitle) =>
        nameOfTitle is not null && title.Length > 0 ? nameOfTitle(title) : null;

    private static bool TryGetLong(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out JsonElement number)
            && number.ValueKind == JsonValueKind.Number
            && number.TryGetInt64(out value);
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
