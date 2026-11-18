using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.CloudDrives.Dropbox;

/// <summary>
/// The Dropbox API v2 as the connector uses it (ADR 0267 §6): requests (RPC endpoints with a JSON
/// body, content endpoints with the <c>Dropbox-API-Arg</c> header), metadata, and errors.
/// Built with <see cref="Utf8JsonWriter"/> and <see cref="JsonDocument"/>, which trimming keeps.
/// </summary>
internal static class DropboxApi
{
    /// <summary>Host of the RPC endpoints.</summary>
    public static readonly Uri ApiBase = new("https://api.dropboxapi.com/2/");

    /// <summary>Host of the content endpoints (download, upload).</summary>
    public static readonly Uri ContentBase = new("https://content.dropboxapi.com/2/");

    /// <summary>Largest file the simple upload accepts (150 MB); the OS stops at 128 MiB first (ADR 0245).</summary>
    public const long MaxUploadBytes = 150L * 1000 * 1000;

    /// <summary>Entries asked per page of <c>files/list_folder</c>.</summary>
    public const int ListPageSize = 2000;

    /// <summary>A call to an RPC endpoint, e.g. <c>files/get_metadata</c>, with a JSON body or none.</summary>
    public static HttpRequestMessage Rpc(string endpoint, Action<Utf8JsonWriter>? body)
    {
        HttpRequestMessage request = new(HttpMethod.Post, new Uri(ApiBase, endpoint));
        if (body is not null)
        {
            ByteArrayContent content = new(Json(body));
            // Dropbox refuses a Content-Type with parameters it does not know: no charset.
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Content = content;
        }

        return request;
    }

    /// <summary>A call to a content endpoint, its argument in the <c>Dropbox-API-Arg</c> header.</summary>
    public static HttpRequestMessage Content(string endpoint, Action<Utf8JsonWriter> argument, byte[]? upload)
    {
        HttpRequestMessage request = new(HttpMethod.Post, new Uri(ContentBase, endpoint));
        // The header must be ASCII: the default encoder writes every other character as \uXXXX.
        request.Headers.TryAddWithoutValidation("Dropbox-API-Arg", Encoding.ASCII.GetString(Json(argument)));
        if (upload is not null)
        {
            ByteArrayContent content = new(upload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            request.Content = content;
        }

        return request;
    }

    /// <summary>Reads a file or folder from Dropbox metadata; <see langword="null"/> for a deleted entry.</summary>
    public static HostFolderEntry? ReadEntry(JsonElement metadata)
    {
        string tag = metadata.TryGetProperty(".tag", out JsonElement tagElement) ? tagElement.GetString() ?? string.Empty : string.Empty;
        string name = metadata.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
        if (name.Length == 0)
        {
            return null;
        }

        switch (tag)
        {
            case "folder":
                return new HostFolderEntry(name, HostFolderEntryKind.Directory, 0, null);
            case "file":
                long size = metadata.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long value) ? value : 0;
                DateTimeOffset? modified = metadata.TryGetProperty("server_modified", out JsonElement modifiedElement)
                    && DateTimeOffset.TryParse(modifiedElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed)
                        ? parsed
                        : null;
                string? rev = metadata.TryGetProperty("rev", out JsonElement revElement) ? revElement.GetString() : null;
                return new HostFolderEntry(name, HostFolderEntryKind.File, size, modified) { Revision = rev };
            default:
                return null;
        }
    }

    /// <summary>Reads the answer of a successful call as JSON.</summary>
    /// <exception cref="HostFolderException">The answer is not JSON.</exception>
    public static async ValueTask<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new HostFolderException(HostFolderErrorKind.Failure, "Dropbox sent an answer Kazo OS cannot read.", exception);
        }
    }

    /// <summary>
    /// Turns a failed answer into the failure of a folder operation. Dropbox answers 409 with an
    /// <c>error_summary</c> such as <c>path/not_found/..</c> for the expected errors.
    /// </summary>
    /// <param name="response">The failed answer.</param>
    /// <param name="conflict">The kind a <c>conflict</c> means: <see cref="HostFolderErrorKind.AlreadyExists"/>, or <see cref="HostFolderErrorKind.RevisionConflict"/> for a conditional write.</param>
    /// <param name="cancellationToken">Cancels the read of the body.</param>
    public static async ValueTask<HostFolderException> ErrorAsync(
        HttpResponseMessage response,
        HostFolderErrorKind conflict,
        CancellationToken cancellationToken)
    {
        string body = string.Empty;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // The status alone says enough.
        }

        string summary = Summary(body);
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return new HostFolderException(HostFolderErrorKind.NotAllowed, $"Dropbox refused the access ({Short(summary, body)}).");
        }

        if (response.StatusCode != HttpStatusCode.Conflict)
        {
            return new HostFolderException(HostFolderErrorKind.Failure, $"Dropbox answered {(int)response.StatusCode} ({Short(summary, body)}).");
        }

        HostFolderErrorKind kind = Classify(summary, conflict);
        return new HostFolderException(kind, $"Dropbox: {summary}");
    }

    /// <summary>The kind of an <c>error_summary</c>.</summary>
    internal static HostFolderErrorKind Classify(string summary, HostFolderErrorKind conflict) =>
        summary switch
        {
            _ when summary.Contains("not_found", StringComparison.Ordinal) => HostFolderErrorKind.NotFound,
            _ when summary.Contains("not_file", StringComparison.Ordinal) || summary.Contains("not_folder", StringComparison.Ordinal) => HostFolderErrorKind.TypeMismatch,
            _ when summary.Contains("insufficient_space", StringComparison.Ordinal) => HostFolderErrorKind.NoSpace,
            _ when summary.Contains("disallowed_name", StringComparison.Ordinal) || summary.Contains("malformed_path", StringComparison.Ordinal) => HostFolderErrorKind.InvalidName,
            _ when summary.Contains("no_write_permission", StringComparison.Ordinal) || summary.Contains("restricted_content", StringComparison.Ordinal) => HostFolderErrorKind.NotAllowed,
            _ when summary.Contains("conflict", StringComparison.Ordinal) => conflict,
            _ => HostFolderErrorKind.Failure
        };

    private static string Summary(string body)
    {
        if (body.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error_summary", out JsonElement summary)
                    ? summary.GetString() ?? string.Empty
                    : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string Short(string summary, string body)
    {
        string text = summary.Length > 0 ? summary : body;
        return text.Length <= 200 ? text : text[..200];
    }

    private static byte[] Json(Action<Utf8JsonWriter> write)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
