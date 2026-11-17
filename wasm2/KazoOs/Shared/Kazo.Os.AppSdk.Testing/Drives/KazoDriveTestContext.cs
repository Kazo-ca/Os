using Kazo.Os.AppSdk.Drives;

namespace Kazo.Os.AppSdk.Testing.Drives;

/// <summary>
/// A provider context for tests (ADR 0282 §2): what the OS lends a drive, filled by the test
/// instead of the OS. Give it to <see cref="KazoDriveOpenRequest"/> to open a drive of a provider
/// without Kazo OS: set <see cref="AccessToken"/> for an OAuth connection, or
/// <see cref="Secrets"/> for the secret fields of a form connection.
/// </summary>
/// <example>
/// <code>
/// KazoDriveTestContext context = new("webdav") { Secrets = { ["password"] = "s3cret" } };
/// KazoDriveOpenRequest request = new("Work", context) { Options = new Dictionary&lt;string, string&gt; { ["server"] = "https://dav.example.org/" } };
/// await using KazoDriveFolder drive = await new WebDavDriveProvider().OpenAsync(request);
/// </code>
/// </example>
public sealed class KazoDriveTestContext : IKazoDriveProviderContext
{
    private int _availabilityNotifications;
    private int _tokenRequests;

    /// <summary>Creates a context for a drive of <paramref name="type"/>.</summary>
    /// <param name="type">The filesystem type, as the feature declares it (<c>webdav</c>).</param>
    /// <param name="account">The signed-in account (OAuth connection), or <see langword="null"/> for a form connection.</param>
    public KazoDriveTestContext(string type, KazoDriveAccount? account = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        Type = type;
        Account = account;
    }

    /// <inheritdoc />
    public string Type { get; }

    /// <inheritdoc />
    public KazoDriveAccount? Account { get; }

    /// <summary>
    /// Gets or sets the access token <see cref="GetAccessTokenAsync"/> returns. <see langword="null"/>
    /// (the default): it throws <see cref="KazoDriveErrorKind.SignInRequired"/>, as the OS does for a
    /// form connection or an account whose sign-in the service refused.
    /// </summary>
    public string? AccessToken { get; set; }

    /// <summary>Gets the values of the secret form fields, by field name, as the OS would unseal them.</summary>
    public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets how many times the provider asked for an access token.</summary>
    public int TokenRequests => Volatile.Read(ref _tokenRequests);

    /// <summary>Gets how many times the provider called <see cref="NotifyAvailabilityChanged"/>.</summary>
    public int AvailabilityNotifications => Volatile.Read(ref _availabilityNotifications);

    /// <inheritdoc />
    public ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _tokenRequests);
        return AccessToken is { } token
            ? ValueTask.FromResult(token)
            : ValueTask.FromException<string>(new KazoDriveException(KazoDriveErrorKind.SignInRequired, "The test context has no access token."));
    }

    /// <inheritdoc />
    public ValueTask<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(name);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Secrets.TryGetValue(name, out string? value) ? value : null);
    }

    /// <inheritdoc />
    public void NotifyAvailabilityChanged() => Interlocked.Increment(ref _availabilityNotifications);
}
