using System.Text.Json;

namespace Kazo.Os.AppSdk.Craft;

/// <summary>
/// The Craft app opened its file picker (<c>rfd</c>'s <c>input type=file</c>, message <c>pick</c>):
/// the window shows the OS dialog instead and answers with <see cref="CraftFrame.AnswerPickAsync"/>.
/// What the pick is for (a document, a picture, fonts...) is the app's call: it tells from
/// <see cref="Extensions"/>, with <see cref="AcceptsAny"/> and <see cref="AcceptsOnly"/>.
/// </summary>
/// <param name="Id">The pick's id, given back with the answer.</param>
/// <param name="Accept">The input's <c>accept</c>: extensions separated by commas (<c>.png,.jpg</c>).</param>
/// <param name="Multiple">Whether several files may be chosen.</param>
public sealed record PickRequest(int Id, string Accept, bool Multiple)
{
    /// <summary>Gets the accepted extensions, lowercase with their dot, without duplicates.</summary>
    public IReadOnlyList<string> Extensions => ParseExtensions(Accept);

    /// <summary>Gets whether the picker accepts at least one of <paramref name="extensions"/>.</summary>
    /// <param name="extensions">Extensions with their dot (compared ignoring case).</param>
    /// <returns><see langword="true"/> when one of them is accepted.</returns>
    public bool AcceptsAny(IEnumerable<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        IReadOnlyList<string> accepted = Extensions;
        return extensions.Any(extension => accepted.Contains(extension.ToLowerInvariant(), StringComparer.Ordinal));
    }

    /// <summary>
    /// Gets whether every accepted extension is one of <paramref name="extensions"/>; a picker with no
    /// extension (any file) accepts more, so it gives <see langword="false"/>.
    /// </summary>
    /// <param name="extensions">Extensions with their dot (compared ignoring case).</param>
    /// <returns><see langword="true"/> when the picker accepts nothing else.</returns>
    public bool AcceptsOnly(IEnumerable<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        HashSet<string> allowed = new(extensions.Select(extension => extension.ToLowerInvariant()), StringComparer.Ordinal);
        IReadOnlyList<string> accepted = Extensions;
        return accepted.Count > 0 && accepted.All(allowed.Contains);
    }

    /// <summary>Reads the JSON <c>craft-host.js</c> passes: <c>{ id, accept, multiple }</c>.</summary>
    /// <param name="json">The message's value.</param>
    /// <returns>The request, or <see langword="null"/> when the value is not one.</returns>
    public static PickRequest? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("id", out JsonElement id)
                || id.ValueKind != JsonValueKind.Number
                || !id.TryGetInt32(out int pickId))
            {
                return null;
            }

            string accept = root.TryGetProperty("accept", out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
            bool multiple = root.TryGetProperty("multiple", out JsonElement many) && many.ValueKind == JsonValueKind.True;
            return new PickRequest(pickId, accept, multiple);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<string> ParseExtensions(string accept) =>
        [.. (accept ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(entry => entry.StartsWith('.'))
            .Select(entry => entry.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)];
}
