using System.Text.Json;

namespace Kazo.Os.AppSdk.Craft;

/// <summary>One document a Craft app has open, as <c>ui.inspect</c> lists it (<c>documents[]</c>).</summary>
/// <param name="Id">The app's id of the document, for <c>engine.execute</c>'s <c>doc</c>.</param>
/// <param name="Name">Its file name (<c>path</c> in the app's web build), or <see langword="null"/> before its first save.</param>
/// <param name="Title">Its title (the name without extension).</param>
/// <param name="Dirty">The app says it has unsaved changes.</param>
/// <param name="Active">It is the document the app shows.</param>
public sealed record OpenDocument(long Id, string? Name, string Title, bool Dirty, bool Active)
{
    /// <summary>Reads the documents of a <c>ui.inspect</c> answer.</summary>
    /// <param name="inspect">The app's answer.</param>
    /// <returns>The documents; empty when the answer has none, or is not the expected shape.</returns>
    public static IReadOnlyList<OpenDocument> ListFrom(JsonElement inspect)
    {
        if (inspect.ValueKind != JsonValueKind.Object
            || !inspect.TryGetProperty("documents", out JsonElement documents)
            || documents.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        List<OpenDocument> list = [];
        foreach (JsonElement document in documents.EnumerateArray())
        {
            if (document.ValueKind != JsonValueKind.Object
                || !document.TryGetProperty("id", out JsonElement id)
                || !id.TryGetInt64(out long value))
            {
                continue;
            }

            list.Add(new OpenDocument(
                value,
                Text(document, "path"),
                Text(document, "title") ?? string.Empty,
                Flag(document, "dirty"),
                Flag(document, "active")));
        }

        return list;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;
}
