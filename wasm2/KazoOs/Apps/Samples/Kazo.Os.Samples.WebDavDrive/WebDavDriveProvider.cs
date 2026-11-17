using Kazo.Os.AppSdk.Drives;

namespace Kazo.Os.Samples.WebDavDrive;

/// <summary>The names of the form fields of a WebDAV drive, as the feature declares them.</summary>
public static class WebDavFields
{
    /// <summary>The server's WebDAV folder, <c>https</c> (<c>https://cloud.example.org/remote.php/dav/files/ben/</c>).</summary>
    public const string Server = "server";

    /// <summary>The user name of HTTP Basic.</summary>
    public const string UserName = "username";

    /// <summary>The password (or app password), a secret the OS seals.</summary>
    public const string Password = "password";
}

/// <summary>
/// The provider of the <c>webdav</c> drive type (ADR 0282): a sample a third party can copy. It
/// reads the address and user name the user typed in the OS's form, asks the OS for the sealed
/// password on each request, and talks WebDAV straight from the browser.
/// </summary>
public sealed class WebDavDriveProvider : KazoDriveProvider
{
    private readonly IWebDavTransport? _transport;

    /// <summary>Creates the provider the OS loads: requests go through the browser's <c>fetch</c>.</summary>
    public WebDavDriveProvider()
    {
    }

    /// <summary>Creates a provider that sends through <paramref name="transport"/> (tests: a local server).</summary>
    /// <param name="transport">How requests are sent.</param>
    internal WebDavDriveProvider(IWebDavTransport transport) => _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    /// <inheritdoc />
    /// <remarks>Checks the form's values only: the OS calls it at login, which never waits on the network.</remarks>
    public override ValueTask<KazoDriveFolder> OpenAsync(KazoDriveOpenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.Options.TryGetValue(WebDavFields.Server, out string? address)
            || !Uri.TryCreate(address, UriKind.Absolute, out Uri? server)
            || server.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(server.UserInfo))
        {
            throw new KazoDriveException(KazoDriveErrorKind.Other, "The drive has no https server address.");
        }

        string userName = request.Options.TryGetValue(WebDavFields.UserName, out string? name) ? name : string.Empty;
        KazoDrivePath? root = null;
        if (request.Root is { } rootText && !KazoDrivePath.TryParse(rootText, out root))
        {
            throw new KazoDriveException(KazoDriveErrorKind.InvalidName, "The drive's root folder is not a valid path.");
        }

        KazoDriveFolder folder = new WebDavDriveFolder(_transport ?? new DirectHttpWebDavTransport(), request.Context, server, root, userName);
        return ValueTask.FromResult(folder);
    }
}
