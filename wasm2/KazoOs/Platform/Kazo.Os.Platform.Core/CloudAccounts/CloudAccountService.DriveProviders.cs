using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kazo.Os.AppSdk.Drives;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Microsoft.Extensions.Logging;

namespace Kazo.Os.Platform.Core.CloudAccounts;

/// <summary>
/// The accounts of the drive types features provide (ADR 0282 §3): accounts of a form connection,
/// and the context the OS lends a provider for the one drive it opens.
/// </summary>
public sealed partial class CloudAccountService
{
    /// <summary>Prefix of the provider account id of a form account: a hash of its non-secret values, never a secret.</summary>
    internal const string FormAccountIdPrefix = "form:";

    /// <summary>
    /// Adds an account of a drive type a feature declares with a form (ADR 0282 §3), or gives new
    /// secrets to the account of that type whose non-secret values are the same (the same address
    /// and user name: the user typed a new password). The non-secret values stay readable in the
    /// account list; the secret ones are sealed on the device under a key of the keyring (ADR 0269)
    /// and are only given to the provider of a drive of this account
    /// (<see cref="CreateDriveProviderContextAsync"/>).
    /// </summary>
    /// <param name="type">The declared drive type, e.g. <c>webdav</c>.</param>
    /// <param name="featureId">The feature that declares the type: the account is only ever lent to its provider.</param>
    /// <param name="displayName">What the account list shows, e.g. the address of the server.</param>
    /// <param name="options">The non-secret values, by field name.</param>
    /// <param name="secrets">The secret values, by field name.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>The account; its <see cref="CloudAccount.Options"/> are <paramref name="options"/>.</returns>
    /// <exception cref="ArgumentException">The type, the feature, the name or a field name is empty.</exception>
    /// <exception cref="CloudAccountException"><see cref="CloudAccountError.KeyringLocked"/> or <see cref="CloudAccountError.NotSignedIn"/>.</exception>
    public async ValueTask<CloudAccount> SaveFormAccountAsync(
        string type,
        string featureId,
        string displayName,
        IReadOnlyDictionary<string, string> options,
        IReadOnlyDictionary<string, string> secrets,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(secrets);
        if (options.Keys.Concat(secrets.Keys).Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A form field has no name.", nameof(options));
        }

        Dictionary<string, string> copiedOptions = new(options, StringComparer.Ordinal);
        byte[] secret = JsonSerializer.SerializeToUtf8Bytes(
            new Dictionary<string, string>(secrets, StringComparer.Ordinal),
            CloudAccountJsonContext.Default.DictionaryStringString);
        try
        {
            return await SaveKeysAccountCoreAsync(type, displayName.Trim(), FormAccountId(copiedOptions), secret, copiedOptions, featureId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// Creates what the OS lends the provider of a declared drive type for one drive (ADR 0282 §3):
    /// the drive's account, its access token (renewed by the OS) or the secret values of its form.
    /// The context is bound to <paramref name="accountId"/>: it can never reach another account,
    /// and it gives nothing once the account is removed, is not of <paramref name="type"/>, or was
    /// added with another feature's provider of that type. The refresh token never leaves the OS.
    /// </summary>
    /// <param name="type">The drive type the provider serves, e.g. <c>pcloud</c>.</param>
    /// <param name="featureId">The feature whose provider opens the drive.</param>
    /// <param name="accountId">The account of the drive being opened.</param>
    /// <param name="availabilityChanged">Called when the provider reports that its service may be reachable again; <see langword="null"/> ignores it.</param>
    /// <param name="cancellationToken">Cancels the read of the account list.</param>
    /// <returns>The context.</returns>
    /// <exception cref="CloudAccountException">
    /// <see cref="CloudAccountError.AccountNotFound"/> when the account is not in the list, is not
    /// of <paramref name="type"/> or belongs to another feature, or <see cref="CloudAccountError.NotSignedIn"/>.
    /// </exception>
    public async ValueTask<IKazoDriveProviderContext> CreateDriveProviderContextAsync(
        string type,
        string featureId,
        string accountId,
        Action? availabilityChanged = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(featureId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        AuthenticatedPrincipal principal = RequirePrincipal();
        List<CloudAccountRecord> records = await _store.LoadAsync(principal, cancellationToken).ConfigureAwait(false);
        CloudAccountRecord account = records.FirstOrDefault(record =>
                string.Equals(record.Id, accountId, StringComparison.Ordinal)
                && string.Equals(record.Provider, type, StringComparison.Ordinal)
                && string.Equals(record.FeatureId, featureId, StringComparison.Ordinal))
            ?? throw NotFound(accountId);
        return new DriveProviderContext(this, type, featureId, account.Id, account.DisplayName, account.HasKeys, availabilityChanged);
    }

    /// <summary>
    /// Gets an access token of an OAuth account of <paramref name="type"/> for its provider's context.
    /// An account of another type or of another feature, or a form account, is refused in the same read.
    /// </summary>
    internal ValueTask<string> GetProviderAccessTokenAsync(string type, string featureId, string accountId, CancellationToken cancellationToken) =>
        GetAccessTokenCoreAsync(accountId, rejectedAccessToken: null, expectedProvider: type, cancellationToken, expectedFeature: featureId);

    /// <summary>
    /// Opens the secret values of a form account of <paramref name="type"/> for its provider's
    /// context; an account of another type or of another feature reads as not found.
    /// </summary>
    /// <returns>The secret values by field name.</returns>
    internal async ValueTask<IReadOnlyDictionary<string, string>> GetFormSecretsAsync(string type, string featureId, string accountId, CancellationToken cancellationToken)
    {
        (_, byte[] secret) = await GetKeysAsync(accountId, type, cancellationToken, featureId).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize(secret, CloudAccountJsonContext.Default.DictionaryStringString)
                ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning("The form values of cloud account {AccountId} cannot be read.", accountId);
            throw new CloudAccountException(CloudAccountError.ReconnectRequired, "The values of this drive must be typed again.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>
    /// The provider account id of a form account: a SHA-256 of its non-secret values, so the same
    /// address and user name find the same account again. Secrets never enter it.
    /// </summary>
    internal static string FormAccountId(IReadOnlyDictionary<string, string> options)
    {
        StringBuilder canonical = new();
        foreach (KeyValuePair<string, string> pair in options.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            // Lengths first, so "a"+"bc" and "ab"+"c" never give the same text.
            canonical.Append(pair.Key.Length).Append(':').Append(pair.Key)
                .Append(pair.Value.Length).Append(':').Append(pair.Value);
        }

        return FormAccountIdPrefix + OAuthPkce.Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
