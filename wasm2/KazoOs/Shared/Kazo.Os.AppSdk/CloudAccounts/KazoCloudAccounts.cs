namespace Kazo.Os.AppSdk.CloudAccounts;

/// <summary>
/// The signed-in user's cloud accounts (ADR 0267 §5, ADR 0270): Dropbox, OneDrive and S3 storage
/// mounted as network drives with <see cref="IO.KazoDriveInfo.MountNetworkDriveAsync"/>. Needs the
/// dangerous capability <c>cloud-accounts.manage</c>; the tokens and keys never leave the OS.
/// </summary>
/// <example>
/// <code>
/// await KazoCloudAccounts.PrepareAsync();                       // when the dialog opens
/// // inside the click:
/// KazoCloudAccount account = await KazoCloudAccounts.ConnectAsync("dropbox", KazoCloudAccessScope.AppFolder);
/// await KazoDriveInfo.MountNetworkDriveAsync(new KazoNetworkDriveRequest("dropbox", account.Id));
/// </code>
/// </example>
public static class KazoCloudAccounts
{
    /// <summary>Gets whether the signed-in user can connect an account: their keyring is open (not the guest).</summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    public static async Task<bool> CanConnectAsync(CancellationToken cancellationToken = default) =>
        await Gateway.CanConnectAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Loads the sign-in window's script ahead of the user's click, so the click opens the window at
    /// once (the browser blocks a window opened too late). Call it when the dialog opens.
    /// </summary>
    /// <param name="cancellationToken">Cancels the preparation.</param>
    public static async Task PrepareAsync(CancellationToken cancellationToken = default) =>
        await Gateway.PrepareAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Gets the services this OS is set up to connect (a service without an app id is not offered).</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<KazoCloudProvider>> GetProvidersAsync(CancellationToken cancellationToken = default) =>
        await Gateway.GetProvidersAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Gets the signed-in user's cloud accounts.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<KazoCloudAccount>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
        await Gateway.GetAccountsAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Opens the service's sign-in window and adds the account. Call it from a click: the browser
    /// opens a window only right after a user gesture.
    /// </summary>
    /// <param name="providerId"><c>dropbox</c> or <c>onedrive</c>.</param>
    /// <param name="scope">The part of the storage the user chose.</param>
    /// <param name="cancellationToken">Stops waiting for the window.</param>
    /// <exception cref="KazoCloudAccountException">The sign-in failed; <see cref="KazoCloudAccountException.Error"/> says why.</exception>
    /// <exception cref="UnauthorizedAccessException">The app lacks <c>cloud-accounts.manage</c>.</exception>
    public static async Task<KazoCloudAccount> ConnectAsync(string providerId, KazoCloudAccessScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        return await Gateway.ConnectAsync(providerId, scope, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Signs in again to an account whose sign-in expired (<see cref="KazoCloudAccountState.NeedsReconnect"/>);
    /// its drives reconnect. Call it from a click.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">Stops waiting for the window.</param>
    /// <exception cref="KazoCloudAccountException">The sign-in failed, or the user signed in to another account.</exception>
    public static async Task<KazoCloudAccount> ReconnectAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        return await Gateway.ReconnectAsync(accountId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Removes an account: unmounts its drives, revokes its sign-in where the service allows it, and forgets it.</summary>
    /// <param name="accountId">The account.</param>
    /// <param name="cancellationToken">Cancels the removal.</param>
    public static async Task RemoveAsync(string accountId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        await Gateway.RemoveAsync(accountId, cancellationToken).ConfigureAwait(false);
    }

    private static IAppCloudAccountsGateway Gateway => KazoExecutionContext.Current.CloudAccounts;
}
