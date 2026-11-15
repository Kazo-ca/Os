using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Platform.Core.Keyring;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Kazo.Os.Simulation.Abstractions.Settings;
using Kazo.Os.Simulation.Abstractions.Time;

namespace Kazo.Os.Platform.Core.CloudAccounts;

/// <summary>
/// Where the cloud accounts of a user live (ADR 0267 section 5), on this device only:
/// <list type="bullet">
/// <item><c>~/.local/state/kazoos/cloud-accounts.json</c> (<c>0600</c>): the accounts, and each
/// account's tokens sealed with AES-256-GCM. <c>~/.local/state</c> is device-local: the
/// synchronization never sends it (ADR 0044 section 10).</item>
/// <item>The user's login keyring (ADR 0100): one entry per account holding the random key that
/// seals its tokens. The keyring opens only with the user's password or PRF passkey, so the
/// tokens are as protected as every other secret of the keyring.</item>
/// </list>
/// The tokens themselves are not written into the keyring because the keyring file is
/// synchronized with the user's home: tokens must never leave the device.
/// </summary>
internal sealed class CloudAccountStore
{
    /// <summary>The account list, relative to the home folder.</summary>
    public const string TableRelativePath = ".local/state/kazoos/cloud-accounts.json";

    /// <summary>Owner of the keyring entries, reserved to the OS: apps can never read them, even with <c>keyring.read-all</c>.</summary>
    public const string KeyringOwner = "org.kazoos.cloud-accounts";

    private const ushort TableMode = 0x180; // 0600
    private const int KeyLength = 32;
    private const int NonceLength = 12;

    private readonly IFileSystemService _fileSystem;
    private readonly ISimulationClock _clock;
    private readonly UserKeyringService _keyring;
    private readonly IKeyringCipher _cipher;

    public CloudAccountStore(IFileSystemService fileSystem, ISimulationClock clock, UserKeyringService keyring, IKeyringCipher cipher)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _keyring = keyring ?? throw new ArgumentNullException(nameof(keyring));
        _cipher = cipher ?? throw new ArgumentNullException(nameof(cipher));
    }

    /// <summary>Gets whether <paramref name="principal"/>'s keyring is open, the condition to keep tokens.</summary>
    public bool IsKeyringUnlocked(AuthenticatedPrincipal principal) => _keyring.IsUnlockedFor(principal.UserId);

    /// <summary>Gets the path of a user's account list.</summary>
    public static VirtualPath TablePath(string homePath) => VirtualPath.Parse($"{homePath.TrimEnd('/')}/{TableRelativePath}");

    /// <summary>Reads the user's accounts; a missing or unreadable list reads as empty.</summary>
    public async ValueTask<List<CloudAccountRecord>> LoadAsync(AuthenticatedPrincipal principal, CancellationToken cancellationToken)
    {
        SettingsFileReadResult read = await SettingsTextFile
            .ReadAsync(Gateway(principal), TablePath(principal.HomePath), cancellationToken)
            .ConfigureAwait(false);
        if (read.Content is not { Length: > 0 } content)
        {
            return [];
        }

        try
        {
            CloudAccountTable? table = JsonSerializer.Deserialize(content, CloudAccountJsonContext.Default.CloudAccountTable);
            return table?.Accounts is { } accounts ? [.. accounts] : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Writes the user's accounts, creating the list with mode <c>0600</c>.</summary>
    public async ValueTask SaveAsync(AuthenticatedPrincipal principal, IReadOnlyList<CloudAccountRecord> accounts, CancellationToken cancellationToken)
    {
        IAppFileSystemGateway files = Gateway(principal);
        VirtualPath path = TablePath(principal.HomePath);
        FileSystemError? directory = await SettingsTextFile
            .EnsureDirectoryAsync(files, SettingsTextFile.GetParent(path), cancellationToken)
            .ConfigureAwait(false);
        if (directory is not null)
        {
            throw new IOException($"Cannot create '{SettingsTextFile.GetParent(path).Value}': {directory.Code}.");
        }

        string text = JsonSerializer.Serialize(new CloudAccountTable(1, accounts), CloudAccountJsonContext.Default.CloudAccountTable);
        FileSystemError? error = await SettingsTextFile
            .EditAsync(files, path, _ => text, createIfMissing: true, TableMode, cancellationToken)
            .ConfigureAwait(false);
        if (error is not null)
        {
            throw new IOException($"Cannot save '{path.Value}': {error.Code}.");
        }
    }

    /// <summary>
    /// Seals <paramref name="tokens"/> under the account's key, creating the key in the keyring when
    /// the account has none yet.
    /// </summary>
    public async ValueTask<SealedTokens> SealAsync(
        AuthenticatedPrincipal principal,
        string accountId,
        string label,
        OAuthTokenSet tokens,
        CancellationToken cancellationToken)
    {
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(
            new TokenPayload(tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAtUtc),
            CloudAccountJsonContext.Default.TokenPayload);
        try
        {
            return await SealBytesAsync(principal, accountId, label, plaintext, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Seals any secret of an account (the tokens of an OAuth account, the keys of an account that
    /// signs its own requests, such as S3) under the account's key, creating the key in the keyring
    /// when the account has none yet. The caller clears <paramref name="plaintext"/>.
    /// </summary>
    public async ValueTask<SealedTokens> SealBytesAsync(
        AuthenticatedPrincipal principal,
        string accountId,
        string label,
        byte[] plaintext,
        CancellationToken cancellationToken)
    {
        byte[] key = await GetKeyAsync(principal, accountId, cancellationToken).ConfigureAwait(false)
            ?? await CreateKeyAsync(principal, accountId, label, cancellationToken).ConfigureAwait(false);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        try
        {
            byte[] sealedData = await _cipher.EncryptAsync(key, nonce, plaintext, cancellationToken).ConfigureAwait(false);
            return new SealedTokens(Convert.ToBase64String(nonce), Convert.ToBase64String(sealedData));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Opens an account's tokens, or returns <see langword="null"/> when they cannot be opened: no
    /// key in the keyring (an administrator reset the password, which sets the keyring aside), or
    /// sealed data that was altered.
    /// </summary>
    public async ValueTask<OAuthTokenSet?> UnsealAsync(AuthenticatedPrincipal principal, CloudAccountRecord account, CancellationToken cancellationToken)
    {
        byte[]? plaintext = await UnsealBytesAsync(principal, account, cancellationToken).ConfigureAwait(false);
        if (plaintext is null)
        {
            return null;
        }

        try
        {
            TokenPayload? payload = JsonSerializer.Deserialize(plaintext, CloudAccountJsonContext.Default.TokenPayload);
            return payload is { AccessToken.Length: > 0 }
                ? new OAuthTokenSet(payload.AccessToken, payload.RefreshToken, payload.ExpiresAtUtc)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    /// <summary>
    /// Opens the sealed secret of an account, or returns <see langword="null"/> when it cannot be
    /// opened (no key in the keyring, altered data). The caller clears the returned bytes.
    /// </summary>
    public async ValueTask<byte[]?> UnsealBytesAsync(AuthenticatedPrincipal principal, CloudAccountRecord account, CancellationToken cancellationToken)
    {
        if (account.Tokens is not { } sealedTokens
            || await GetKeyAsync(principal, account.Id, cancellationToken).ConfigureAwait(false) is not { } key)
        {
            return null;
        }

        try
        {
            return await _cipher
                .DecryptAsync(key, Convert.FromBase64String(sealedTokens.Nonce), Convert.FromBase64String(sealedTokens.Data), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FormatException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Deletes the account's key from the keyring; its sealed tokens can never be opened again.</summary>
    public ValueTask<bool> DeleteKeyAsync(AuthenticatedPrincipal principal, string accountId, CancellationToken cancellationToken) =>
        _keyring.DeleteAsync(principal.UserId, KeyringOwner, accountId, cancellationToken);

    private async ValueTask<byte[]?> GetKeyAsync(AuthenticatedPrincipal principal, string accountId, CancellationToken cancellationToken)
    {
        string? secret = await _keyring.GetAsync(principal.UserId, KeyringOwner, accountId, cancellationToken).ConfigureAwait(false);
        if (secret is null)
        {
            return null;
        }

        try
        {
            byte[] key = Convert.FromBase64String(secret);
            return key.Length == KeyLength ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async ValueTask<byte[]> CreateKeyAsync(AuthenticatedPrincipal principal, string accountId, string label, CancellationToken cancellationToken)
    {
        byte[] key = RandomNumberGenerator.GetBytes(KeyLength);
        await _keyring
            .SetAsync(principal.UserId, KeyringOwner, accountId, Convert.ToBase64String(key), label, cancellationToken)
            .ConfigureAwait(false);
        return key;
    }

    /// <summary>The user's own file access, as the keyring daemon writes the keyring: the list belongs to the user.</summary>
    private IAppFileSystemGateway Gateway(AuthenticatedPrincipal principal) => new AppFileSystemGateway(
        _fileSystem,
        _clock,
        new AppOperationContext
        {
            AppId = KeyringOwner,
            UserId = principal.UserId.ToString(),
            UserName = principal.LoginName.Value,
            UserAuthority = principal.Authority,
            GrantedCapabilities = new HashSet<string>(
                [AppCapabilities.FileSystemUserHomeRead, AppCapabilities.FileSystemUserHomeWrite],
                StringComparer.Ordinal),
            IsSystemOperation = false
        },
        [.. principal.GroupIds.Select(static id => id.ToString())]);
}

/// <summary>Tokens sealed with AES-256-GCM: base64 of the 12-byte nonce, and of the ciphertext followed by the tag.</summary>
/// <param name="Nonce">Base64 nonce, new at every seal.</param>
/// <param name="Data">Base64 ciphertext and tag.</param>
internal sealed record SealedTokens(string Nonce, string Data);

/// <summary>One account of <c>cloud-accounts.json</c>.</summary>
/// <param name="Id">The OS's id of the account.</param>
/// <param name="Provider">Provider id.</param>
/// <param name="Scope">Access scope.</param>
/// <param name="DisplayName">Name shown to the user.</param>
/// <param name="ProviderAccountId">The provider's id of the account, when it gave one.</param>
/// <param name="State">Whether the tokens still work.</param>
/// <param name="ConnectedAtUtc">Last sign-in.</param>
/// <param name="Tokens">The sealed tokens (or keys, see <see cref="Credential"/>), or <see langword="null"/> once forgotten.</param>
internal sealed record CloudAccountRecord(
    string Id,
    string Provider,
    CloudAccessScope Scope,
    string DisplayName,
    string? ProviderAccountId,
    CloudAccountState State,
    DateTimeOffset ConnectedAtUtc,
    SealedTokens? Tokens)
{
    /// <summary>
    /// How the account proves itself: <see langword="null"/> for OAuth tokens (Dropbox, OneDrive),
    /// <see cref="KeysCredential"/> for keys the user typed, with which the OS signs each request (S3).
    /// </summary>
    public string? Credential { get; init; }

    /// <summary>Value of <see cref="Credential"/> for an account that signs its requests with the user's keys.</summary>
    public const string KeysCredential = "keys";

    /// <summary>Gets whether the account holds keys rather than OAuth tokens.</summary>
    [JsonIgnore]
    public bool HasKeys => string.Equals(Credential, KeysCredential, StringComparison.Ordinal);

    public CloudAccount Describe() => new(Id, Provider, Scope, DisplayName, ProviderAccountId, State, ConnectedAtUtc);
}

/// <summary>On-disk shape of <c>cloud-accounts.json</c>.</summary>
/// <param name="SchemaVersion">Format version, 1.</param>
/// <param name="Accounts">The user's accounts.</param>
internal sealed record CloudAccountTable(int SchemaVersion, IReadOnlyList<CloudAccountRecord> Accounts);

/// <summary>Plaintext of <see cref="SealedTokens"/>.</summary>
internal sealed record TokenPayload(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAtUtc);

/// <summary>Source-generated JSON of the account list and the sealed payload.</summary>
[JsonSerializable(typeof(CloudAccountTable))]
[JsonSerializable(typeof(TokenPayload))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class CloudAccountJsonContext : JsonSerializerContext;
