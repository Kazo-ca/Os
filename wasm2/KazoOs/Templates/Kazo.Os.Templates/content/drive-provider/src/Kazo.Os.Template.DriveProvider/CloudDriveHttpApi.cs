using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.AppSdk.Drives;
using Kazo.Os.AppSdk.Net;

namespace Kazo.Os.Template.DriveProvider;

/// <summary>
/// <see cref="ICloudDriveApi"/> over the service's JSON API, sent straight from the browser with
/// <see cref="KazoDirectHttpClient"/> (ADR 0282 §4): no Kazo server in between, so the service must
/// allow the OS's origin in CORS. <c>drives.provide</c> lets the Feature reach
/// <see cref="CloudDriveFeature.ApiHost"/> because its declaration names it (ADR 0284 §1).
/// </summary>
/// <remarks>
/// The API here is a fictional one, written to show the pattern: replace the addresses, the
/// query strings and the JSON shapes with your service's. Keep the mapping of the answers to
/// <see cref="KazoDriveErrorKind"/>: the conformance suite and the OS rely on it.
/// </remarks>
public sealed class CloudDriveHttpApi : ICloudDriveApi, IDisposable
{
    private const string BaseAddress = "https://" + CloudDriveFeature.ApiHost + "/v1";
    private const int ChunkBytes = 81920;

    private readonly KazoDirectHttpClient _client = new() { Timeout = TimeSpan.FromSeconds(60) };
    private readonly IKazoDriveProviderContext _context;

    /// <summary>Creates the API of one drive.</summary>
    /// <param name="context">What the OS lends the drive: the access token of the user's account.</param>
    public CloudDriveHttpApi(IKazoDriveProviderContext context) =>
        _context = context ?? throw new ArgumentNullException(nameof(context));

    /// <inheritdoc />
    public async Task<CloudDriveItem> GetItemAsync(string path, CancellationToken cancellationToken)
    {
        await using KazoDirectHttpResponse response = await SendAsync(HttpMethod.Get, "/items?path=" + Escape(path), null, cancellationToken).ConfigureAwait(false);
        return ToItem(await ReadJsonAsync(response, CloudDriveJson.Default.CloudDriveItemDto, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CloudDriveItem>> GetChildrenAsync(string path, CancellationToken cancellationToken)
    {
        await using KazoDirectHttpResponse response = await SendAsync(HttpMethod.Get, "/children?path=" + Escape(path), null, cancellationToken).ConfigureAwait(false);
        CloudDriveChildrenDto children = await ReadJsonAsync(response, CloudDriveJson.Default.CloudDriveChildrenDto, cancellationToken).ConfigureAwait(false);
        return [.. (children.Items ?? []).Select(ToItem)];
    }

    /// <inheritdoc />
    public async Task<byte[]> DownloadAsync(string path, CancellationToken cancellationToken)
    {
        await using KazoDirectHttpResponse response = await SendAsync(HttpMethod.Get, "/content?path=" + Escape(path), null, cancellationToken).ConfigureAwait(false);
        if (response.ContentLength > KazoDriveLimits.MaxFileBytes)
        {
            throw TooLarge();
        }

        // Read in chunks and stop at the limit: a length the server did not send is not trusted.
        using MemoryStream buffer = new();
        byte[] chunk = new byte[ChunkBytes];
        int read;
        while ((read = await response.Content.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > KazoDriveLimits.MaxFileBytes)
            {
                throw TooLarge();
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <inheritdoc />
    public async Task<CloudDriveItem> UploadAsync(string path, ReadOnlyMemory<byte> content, CloudDriveUploadMode mode, string? revision, CancellationToken cancellationToken)
    {
        if (content.Length > KazoDriveLimits.MaxFileBytes)
        {
            throw TooLarge();
        }

        string query = "/content?path=" + Escape(path) + "&mode=" + (mode == CloudDriveUploadMode.Create ? "create" : "overwrite")
            + (revision is null ? string.Empty : "&revision=" + Escape(revision));
        await using KazoDirectHttpResponse response = await SendAsync(HttpMethod.Put, query, KazoDirectHttpContent.FromBytes(content), cancellationToken).ConfigureAwait(false);
        return ToItem(await ReadJsonAsync(response, CloudDriveJson.Default.CloudDriveItemDto, cancellationToken).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task CreateFolderAsync(string path, CancellationToken cancellationToken)
    {
        await using KazoDirectHttpResponse response = await SendAsync(HttpMethod.Post, "/folders?path=" + Escape(path), null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)
    {
        string query = "/items?path=" + Escape(path) + "&recursive=" + (recursive ? "true" : "false");
        await using KazoDirectHttpResponse response = await SendAsync(HttpMethod.Delete, query, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task MoveAsync(string from, string to, CancellationToken cancellationToken)
    {
        string query = "/move?from=" + Escape(from) + "&to=" + Escape(to);
        await using KazoDirectHttpResponse response = await SendAsync(HttpMethod.Post, query, null, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<KazoDriveQuota?> GetQuotaAsync(CancellationToken cancellationToken)
    {
        await using KazoDirectHttpResponse response = await SendAsync(HttpMethod.Get, "/quota", null, cancellationToken).ConfigureAwait(false);
        CloudDriveQuotaDto quota = await ReadJsonAsync(response, CloudDriveJson.Default.CloudDriveQuotaDto, cancellationToken).ConfigureAwait(false);
        return quota is { Total: >= 0, Used: >= 0 } ? new KazoDriveQuota(quota.Total, Math.Max(0, quota.Total - quota.Used)) : null;
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();

    /// <summary>
    /// Sends one request signed with the account's access token. The OS gives the token and renews
    /// it; when the user must sign in again, <see cref="IKazoDriveProviderContext.GetAccessTokenAsync"/>
    /// already throws <see cref="KazoDriveErrorKind.SignInRequired"/>.
    /// </summary>
    private async Task<KazoDirectHttpResponse> SendAsync(HttpMethod method, string relative, KazoDirectHttpContent? content, CancellationToken cancellationToken)
    {
        string token = await _context.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        KazoDirectHttpRequest request = new(method, new Uri(BaseAddress + relative)) { Content = content };
        request.Headers.Add(new("Authorization", "Bearer " + token));

        KazoDirectHttpResponse response;
        try
        {
            response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            // A network failure, a CORS refusal and a TLS error look the same to a page.
            throw new KazoDriveException(KazoDriveErrorKind.Unavailable, "The service cannot be reached.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new KazoDriveException(KazoDriveErrorKind.Unavailable, "The service did not answer in time.", exception);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        await using (response.ConfigureAwait(false))
        {
            throw await ErrorOfAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Turns an error answer into the kind of error the OS turns into <c>ENOENT</c>, <c>EEXIST</c>…</summary>
    private static async Task<KazoDriveException> ErrorOfAsync(KazoDirectHttpResponse response, CancellationToken cancellationToken)
    {
        string? code = null;
        if (response.StatusCode is 400 or 409)
        {
            try
            {
                code = (await response.ReadFromJsonAsync(CloudDriveJson.Default.CloudDriveErrorDto, cancellationToken).ConfigureAwait(false))?.Error;
            }
            catch (JsonException)
            {
                // No JSON body: the status alone decides.
            }
        }

        KazoDriveErrorKind kind = (response.StatusCode, code) switch
        {
            (401, _) => KazoDriveErrorKind.SignInRequired,
            (403, _) => KazoDriveErrorKind.AccessDenied,
            (404 or 410, _) => KazoDriveErrorKind.NotFound,
            (409, "exists") => KazoDriveErrorKind.Exists,
            (409, "conflict") or (412, _) => KazoDriveErrorKind.RevisionConflict,
            (409, "not_empty") => KazoDriveErrorKind.NotEmpty,
            (409, "not_a_folder" or "is_a_folder") => KazoDriveErrorKind.TypeMismatch,
            (400, "invalid_name") => KazoDriveErrorKind.InvalidName,
            (413, _) => KazoDriveErrorKind.FileTooLarge,
            (507, _) => KazoDriveErrorKind.QuotaExceeded,
            (408 or 429, _) or (>= 500, _) => KazoDriveErrorKind.Unavailable,
            _ => KazoDriveErrorKind.Other,
        };
        return new KazoDriveException(kind, $"The service answered {response.StatusCode}{(code is null ? string.Empty : " (" + code + ")")}.");
    }

    private static async Task<T> ReadJsonAsync<T>(KazoDirectHttpResponse response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        try
        {
            return await response.ReadFromJsonAsync(type, cancellationToken).ConfigureAwait(false)
                ?? throw new KazoDriveException(KazoDriveErrorKind.Other, "The service answered nothing.");
        }
        catch (JsonException exception)
        {
            throw new KazoDriveException(KazoDriveErrorKind.Other, "The service's answer is not the JSON expected.", exception);
        }
    }

    private static CloudDriveItem ToItem(CloudDriveItemDto item) =>
        new(item.Name ?? string.Empty, item.Folder, item.Size, item.Modified, item.Revision);

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static KazoDriveException TooLarge() =>
        new(KazoDriveErrorKind.FileTooLarge, "The file is larger than 128 MiB.");
}

/// <summary>An entry as the service's JSON writes it.</summary>
internal sealed record CloudDriveItemDto(string? Name, bool Folder, long Size, DateTimeOffset? Modified, string? Revision);

/// <summary>The children of a folder.</summary>
internal sealed record CloudDriveChildrenDto(CloudDriveItemDto[]? Items);

/// <summary>The account's storage.</summary>
internal sealed record CloudDriveQuotaDto(long Total, long Used);

/// <summary>The body of an error answer: <c>{"error": "exists"}</c>.</summary>
internal sealed record CloudDriveErrorDto(string? Error);

/// <summary>
/// The JSON of the service, generated at build: no reflection, so the Feature stays trimmable
/// (the host trims it when it publishes).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CloudDriveItemDto))]
[JsonSerializable(typeof(CloudDriveChildrenDto))]
[JsonSerializable(typeof(CloudDriveQuotaDto))]
[JsonSerializable(typeof(CloudDriveErrorDto))]
internal sealed partial class CloudDriveJson : JsonSerializerContext;
