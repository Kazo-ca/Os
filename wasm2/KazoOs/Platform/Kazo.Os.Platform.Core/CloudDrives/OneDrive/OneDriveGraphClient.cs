using System.Net;
using System.Text.Json;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.CloudDrives.OneDrive;

/// <summary>
/// The requests of one OneDrive drive to Microsoft Graph (ADR 0267 §6). Graph requests go through
/// the drive's <see cref="CloudDriveConnection"/> (access token, renewal after a 401, network and
/// 5xx as <see cref="HostFolderErrorKind.Unavailable"/>); the pre-authenticated download and upload
/// addresses Graph hands out go straight to <see cref="HttpClient"/>, never with the token. Graph's
/// refusals become the <see cref="HostFolderException"/> kinds the OS knows.
/// </summary>
internal sealed class OneDriveGraphClient(CloudDriveConnection connection, HttpClient http)
{
    /// <summary>The Microsoft Graph v1.0 endpoint every request starts at.</summary>
    public static readonly Uri GraphBase = new("https://graph.microsoft.com/v1.0/");

    /// <summary>Sends a Graph request (relative to <see cref="GraphBase"/>, or absolute like <c>@odata.nextLink</c>).</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="address">The address.</param>
    /// <param name="content">Builds the body again for each try; <see langword="null"/> for none.</param>
    /// <param name="ifMatch">An <c>If-Match</c> revision, or <see langword="null"/>.</param>
    /// <param name="naming">Whether the request names an entry: a 400 answer then means an invalid name.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The successful answer; the caller disposes it.</returns>
    public async Task<HttpResponseMessage> SendGraphAsync(
        HttpMethod method,
        string address,
        Func<HttpContent>? content,
        string? ifMatch,
        bool naming,
        CancellationToken cancellationToken)
    {
        Uri uri = new(GraphBase, address);
        HttpResponseMessage response = await connection.SendAsync(
            () =>
            {
                HttpRequestMessage request = new(method, uri) { Content = content?.Invoke() };
                if (ifMatch is not null)
                {
                    request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
                }

                return request;
            },
            cancellationToken).ConfigureAwait(false);
        return await EnsureSuccessAsync(response, naming, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a request to a pre-authenticated address Graph handed out (<c>@microsoft.graph.downloadUrl</c>,
    /// an upload session's <c>uploadUrl</c>), without the access token.
    /// </summary>
    public async Task<HttpResponseMessage> SendPreauthenticatedAsync(Func<HttpRequestMessage> build, CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        using (HttpRequestMessage request = build())
        {
            try
            {
                response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                throw new HostFolderException(HostFolderErrorKind.Unavailable, "OneDrive cannot be reached.", exception);
            }
            catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new HostFolderException(HostFolderErrorKind.Unavailable, "OneDrive did not answer in time.", exception);
            }
        }

        if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
        {
            response.Dispose();
            throw new HostFolderException(HostFolderErrorKind.Unavailable, "OneDrive does not answer now.");
        }

        return await EnsureSuccessAsync(response, naming: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Sends a Graph request and reads its JSON answer.</summary>
    public async Task<JsonDocument> GetJsonAsync(
        HttpMethod method,
        string address,
        Func<HttpContent>? content,
        string? ifMatch,
        bool naming,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await SendGraphAsync(method, address, content, ifMatch, naming, cancellationToken).ConfigureAwait(false);
        return await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the JSON body of an answer.</summary>
    /// <exception cref="HostFolderException"><see cref="HostFolderErrorKind.Failure"/> when it is not JSON.</exception>
    public static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new HostFolderException(HostFolderErrorKind.Failure, "Microsoft Graph sent an answer Kazo OS cannot read.", exception);
        }
    }

    /// <summary>Returns a successful answer, or disposes it and throws the error the OS knows.</summary>
    private static async Task<HttpResponseMessage> EnsureSuccessAsync(HttpResponseMessage response, bool naming, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        HttpStatusCode status = response.StatusCode;
        string? code = await ReadErrorCodeAsync(response, cancellationToken).ConfigureAwait(false);
        response.Dispose();
        throw Fail(status, code, naming);
    }

    /// <summary>Turns a refused request into the error the OS knows.</summary>
    internal static HostFolderException Fail(HttpStatusCode status, string? code, bool naming) => status switch
    {
        HttpStatusCode.NotFound => new HostFolderException(HostFolderErrorKind.NotFound, "Nothing is at this path on OneDrive."),
        HttpStatusCode.Conflict => new HostFolderException(HostFolderErrorKind.AlreadyExists, "An entry of OneDrive already has this name."),
        HttpStatusCode.PreconditionFailed => new HostFolderException(HostFolderErrorKind.RevisionConflict, "The file changed on OneDrive since it was read."),
        HttpStatusCode.InsufficientStorage => new HostFolderException(HostFolderErrorKind.NoSpace, "The OneDrive storage is full."),
        HttpStatusCode.RequestEntityTooLarge => new HostFolderException(HostFolderErrorKind.FileTooLarge, "OneDrive refused a file this large."),
        HttpStatusCode.Forbidden when code == "quotaLimitReached" => new HostFolderException(HostFolderErrorKind.NoSpace, "The OneDrive storage is full."),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new HostFolderException(HostFolderErrorKind.NotAllowed, "OneDrive refused this operation."),
        HttpStatusCode.BadRequest when naming => new HostFolderException(HostFolderErrorKind.InvalidName, "OneDrive refuses this name."),
        >= HttpStatusCode.InternalServerError => new HostFolderException(HostFolderErrorKind.Unavailable, "OneDrive does not answer now."),
        _ => new HostFolderException(HostFolderErrorKind.Failure, $"OneDrive answered {(int)status}.")
    };

    /// <summary>Reads Graph's <c>error.code</c>, when the body has one; never throws.</summary>
    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = await ReadJsonAsync(response, cancellationToken).ConfigureAwait(false);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out JsonElement error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("code", out JsonElement code)
                && code.ValueKind == JsonValueKind.String
                    ? code.GetString()
                    : null;
        }
        catch (Exception exception) when (exception is HostFolderException or HttpRequestException or IOException)
        {
            return null;
        }
    }
}
