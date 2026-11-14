using System.Net;
using System.Net.Http.Headers;
using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.CloudDrives;

/// <summary>
/// The link between one cloud drive and its account (ADR 0267 §5 and §9), shared by every
/// connector: it adds the account's access token to each request, renews it once when the
/// provider answers 401, and turns what is not the connector's business into
/// <see cref="HostFolderException"/>: no network, a provider down, an account to reconnect.
/// </summary>
/// <remarks>
/// <para>
/// <b>Permission.</b> <see cref="QueryPermissionAsync"/> answers without the user:
/// <see cref="HostFolderPermission.Prompt"/> when the account needs its sign-in window again,
/// <see cref="HostFolderPermission.Unavailable"/> when no token can be had now (offline renewal),
/// <see cref="HostFolderPermission.Denied"/> when the account was removed, otherwise
/// <see cref="HostFolderPermission.Granted"/>. It never calls the file service: a login is never
/// slowed by a cloud drive, and a drive that went offline is tried again by its next operation.
/// </para>
/// <para>
/// <b>Failures.</b> A network failure, a timeout and the provider's 502/503/504 are
/// <see cref="HostFolderErrorKind.Unavailable"/> (the drive disconnects and reconnects by itself);
/// 429 is <see cref="HostFolderErrorKind.Failure"/>. Every other answer goes back to the connector,
/// which reads its provider's error format.
/// </para>
/// </remarks>
/// <param name="tokens">The cloud accounts.</param>
/// <param name="accountId">The account of the drive.</param>
/// <param name="http">The HTTP client (<c>fetch</c> in the browser; a fake handler in tests).</param>
public sealed class CloudDriveConnection(ICloudAccountTokens tokens, string accountId, HttpClient http)
{
    private readonly ICloudAccountTokens _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    private readonly HttpClient _http = http ?? throw new ArgumentNullException(nameof(http));

    /// <summary>Gets the account of the drive.</summary>
    public string AccountId { get; } = string.IsNullOrEmpty(accountId) ? throw new ArgumentException("An account id is required.", nameof(accountId)) : accountId;

    /// <summary>Gets the accounts the connection reads its tokens from.</summary>
    public ICloudAccountTokens Tokens => _tokens;

    /// <summary>Tells whether the drive can be used now, without the user and without the file service.</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    public async ValueTask<HostFolderPermission> QueryPermissionAsync(CancellationToken cancellationToken = default)
    {
        CloudAccount? account = await _tokens.FindAccountAsync(AccountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return HostFolderPermission.Denied;
        }

        if (account.State == CloudAccountState.NeedsReconnect)
        {
            return HostFolderPermission.Prompt;
        }

        try
        {
            await _tokens.GetAccessTokenAsync(AccountId, cancellationToken).ConfigureAwait(false);
            return HostFolderPermission.Granted;
        }
        catch (CloudAccountException exception)
        {
            return exception.Error switch
            {
                CloudAccountError.ReconnectRequired => HostFolderPermission.Prompt,
                CloudAccountError.AccountNotFound or CloudAccountError.NotSignedIn => HostFolderPermission.Denied,
                _ => HostFolderPermission.Unavailable
            };
        }
    }

    /// <summary>
    /// "Reconnect": opens the provider's sign-in window when the account needs it (a user gesture
    /// is required), otherwise checks again. A closed or refused window leaves the drive as it was.
    /// </summary>
    /// <param name="cancellationToken">Stops waiting for the window.</param>
    /// <exception cref="InvalidOperationException">The browser blocked the window: it needs a recent user gesture.</exception>
    public async ValueTask<HostFolderPermission> RequestPermissionAsync(CancellationToken cancellationToken = default)
    {
        HostFolderPermission permission = await QueryPermissionAsync(cancellationToken).ConfigureAwait(false);
        if (permission != HostFolderPermission.Prompt)
        {
            return permission;
        }

        try
        {
            await _tokens.ReconnectAsync(AccountId, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudAccountException exception) when (exception.Error == CloudAccountError.WindowBlocked)
        {
            throw new InvalidOperationException(exception.Message, exception);
        }
        catch (CloudAccountException exception) when (exception.Error is CloudAccountError.Cancelled or CloudAccountError.Denied or CloudAccountError.AccountMismatch)
        {
            return HostFolderPermission.Prompt;
        }
        catch (CloudAccountException)
        {
            return HostFolderPermission.Unavailable;
        }

        return await QueryPermissionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends a request with the account's token. <paramref name="build"/> is called again for the
    /// one retry after a 401, because a request message cannot be sent twice.
    /// </summary>
    /// <param name="build">Builds the request, without the <c>Authorization</c> header.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <returns>The provider's answer, for any status but those the remarks of this class handle; the caller disposes it.</returns>
    /// <exception cref="HostFolderException">No token, no network, the provider down, or 429.</exception>
    public async ValueTask<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(build);
        string token = await TokenAsync(rejected: null, cancellationToken).ConfigureAwait(false);
        HttpResponseMessage response = await SendOnceAsync(build, token, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            token = await TokenAsync(token, cancellationToken).ConfigureAwait(false);
            response = await SendOnceAsync(build, token, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                throw new HostFolderException(HostFolderErrorKind.Unavailable, "The cloud service refused the account's token; sign in again.");
            }
        }

        switch (response.StatusCode)
        {
            case HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout:
                response.Dispose();
                throw new HostFolderException(HostFolderErrorKind.Unavailable, $"The cloud service is unavailable ({(int)response.StatusCode}).");
            case HttpStatusCode.TooManyRequests:
                TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
                response.Dispose();
                throw new HostFolderException(
                    HostFolderErrorKind.Failure,
                    retryAfter is { } delay
                        ? $"The cloud service asks to slow down; try again in {Math.Ceiling(delay.TotalSeconds)} s."
                        : "The cloud service asks to slow down; try again later.");
            default:
                return response;
        }
    }

    /// <summary>Turns a refusal of the cloud accounts into the failure of a folder operation.</summary>
    /// <param name="exception">The refusal.</param>
    public static HostFolderException ToHostFolderException(CloudAccountException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Error switch
        {
            CloudAccountError.AccountNotFound or CloudAccountError.NotSignedIn =>
                new HostFolderException(HostFolderErrorKind.NotAllowed, exception.Message, exception),
            _ => new HostFolderException(HostFolderErrorKind.Unavailable, exception.Message, exception)
        };
    }

    private async ValueTask<string> TokenAsync(string? rejected, CancellationToken cancellationToken)
    {
        try
        {
            return rejected is null
                ? await _tokens.GetAccessTokenAsync(AccountId, cancellationToken).ConfigureAwait(false)
                : await _tokens.RenewAccessTokenAsync(AccountId, rejected, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudAccountException exception)
        {
            throw ToHostFolderException(exception);
        }
    }

    private async ValueTask<HttpResponseMessage> SendOnceAsync(Func<HttpRequestMessage> build, string token, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = build();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new HostFolderException(HostFolderErrorKind.Unavailable, "The cloud service cannot be reached.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HostFolderException(HostFolderErrorKind.Unavailable, "The cloud service did not answer in time.", exception);
        }
    }
}
