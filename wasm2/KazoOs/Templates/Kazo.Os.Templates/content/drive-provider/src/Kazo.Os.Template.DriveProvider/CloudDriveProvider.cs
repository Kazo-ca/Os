using Kazo.Os.AppSdk.Drives;

namespace Kazo.Os.Template.DriveProvider;

/// <summary>
/// The provider of the <c>template-drive</c> type (ADR 0282 §2). The OS creates it with its public
/// parameterless constructor, then calls <see cref="OpenAsync"/> for each drive a user connects.
/// </summary>
public sealed class CloudDriveProvider : KazoDriveProvider
{
    private readonly Func<IKazoDriveProviderContext, ICloudDriveApi> _connect;

    /// <summary>Creates the provider the OS loads: each drive talks to the real service.</summary>
    public CloudDriveProvider()
        : this(static context => new CloudDriveHttpApi(context))
    {
    }

    /// <summary>Creates a provider whose drives talk to another service (tests: an in-memory one).</summary>
    /// <param name="connect">Gives the API of a drive from the context the OS lends it.</param>
    internal CloudDriveProvider(Func<IKazoDriveProviderContext, ICloudDriveApi> connect) =>
        _connect = connect ?? throw new ArgumentNullException(nameof(connect));

    /// <inheritdoc />
    /// <remarks>
    /// Sends nothing: the OS opens the drives at login, which never waits on the network. An
    /// unreachable service shows when <see cref="KazoDriveFolder.GetStateAsync"/> is asked.
    /// </remarks>
    public override ValueTask<KazoDriveFolder> OpenAsync(KazoDriveOpenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        KazoDrivePath? root = null;
        if (request.Root is { } rootText && !KazoDrivePath.TryParse(rootText, out root))
        {
            throw new KazoDriveException(KazoDriveErrorKind.InvalidName, "The drive's root folder is not a valid path.");
        }

        KazoDriveFolder folder = new CloudDriveFolder(_connect(request.Context), root);
        return ValueTask.FromResult(folder);
    }
}
