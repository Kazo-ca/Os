using Kazo.Os.AppSdk.Drives;

namespace Kazo.Os.Platform.Core.CloudAccounts;

/// <summary>
/// What the OS lends the provider of a declared drive type for one drive (ADR 0282 §3,
/// <see cref="IKazoDriveProviderContext"/>). It is bound to one account, one type and one feature
/// at creation and checks all three again at each call, against the account list as it is then: a provider can never
/// reach another account's token or secrets, and gets nothing once the account is removed. It
/// holds no token or secret itself, and never gives the refresh token, which stays sealed (ADR 0269).
/// </summary>
internal sealed class DriveProviderContext : IKazoDriveProviderContext
{
    private readonly CloudAccountService _accounts;
    private readonly string _featureId;
    private readonly string _accountId;
    private readonly bool _form;
    private readonly Action? _availabilityChanged;

    /// <summary>Initializes the context of one drive.</summary>
    /// <param name="accounts">The account service that keeps the tokens and secrets.</param>
    /// <param name="type">The drive type the provider serves.</param>
    /// <param name="featureId">The feature whose provider the context is lent to.</param>
    /// <param name="accountId">The account of the drive.</param>
    /// <param name="displayName">The account's name, for <see cref="Account"/>.</param>
    /// <param name="form">Whether the account comes from a form (sealed values, no token).</param>
    /// <param name="availabilityChanged">Called by <see cref="NotifyAvailabilityChanged"/>.</param>
    public DriveProviderContext(
        CloudAccountService accounts,
        string type,
        string featureId,
        string accountId,
        string displayName,
        bool form,
        Action? availabilityChanged)
    {
        _accounts = accounts;
        Type = type;
        _featureId = featureId;
        _accountId = accountId;
        _form = form;
        _availabilityChanged = availabilityChanged;
        // A form connection has no sign-in, so no account is shown to the provider (the contract
        // says so); its values still belong to an account the OS keeps.
        Account = form ? null : new KazoDriveAccount(accountId, displayName);
    }

    /// <inheritdoc />
    public string Type { get; }

    /// <inheritdoc />
    public KazoDriveAccount? Account { get; }

    /// <inheritdoc />
    public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_form)
        {
            throw new KazoDriveException(KazoDriveErrorKind.SignInRequired, $"A '{Type}' drive connected with a form has no access token.");
        }

        try
        {
            return await _accounts.GetProviderAccessTokenAsync(Type, _featureId, _accountId, cancellationToken).ConfigureAwait(false);
        }
        catch (CloudAccountException exception)
        {
            throw Translate(exception);
        }
    }

    /// <inheritdoc />
    public async ValueTask<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!_form)
        {
            return null;
        }

        try
        {
            IReadOnlyDictionary<string, string> secrets = await _accounts
                .GetFormSecretsAsync(Type, _featureId, _accountId, cancellationToken)
                .ConfigureAwait(false);
            return secrets.TryGetValue(name, out string? value) ? value : null;
        }
        catch (CloudAccountException exception)
        {
            throw Translate(exception);
        }
    }

    /// <inheritdoc />
    public void NotifyAvailabilityChanged() => _availabilityChanged?.Invoke();

    /// <summary>
    /// Turns a refusal of the account service into the error of the provider contract: what needs
    /// the user (a reconnection, a locked keyring, a removed account) is
    /// <see cref="KazoDriveErrorKind.SignInRequired"/>; an unreachable service is
    /// <see cref="KazoDriveErrorKind.Unavailable"/>. Both disconnect the drive.
    /// </summary>
    internal static KazoDriveException Translate(CloudAccountException exception) =>
        new(exception.Error switch
        {
            CloudAccountError.ProviderUnavailable or CloudAccountError.InvalidResponse => KazoDriveErrorKind.Unavailable,
            CloudAccountError.ReconnectRequired
                or CloudAccountError.KeyringLocked
                or CloudAccountError.NotSignedIn
                or CloudAccountError.AccountNotFound
                or CloudAccountError.ProviderNotConfigured
                or CloudAccountError.Denied => KazoDriveErrorKind.SignInRequired,
            _ => KazoDriveErrorKind.Other
        },
        exception.Message,
        exception);
}
