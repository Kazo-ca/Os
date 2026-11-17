using Kazo.Os.AppSdk.Drives;

namespace Contoso.Sdk35DriveProvider;

/// <summary>
/// The provider of the <c>contoso-memory</c> type (ADR 0282 §2): each drive opens the space its
/// form names, checked against the passphrase the OS lends as a secret.
/// </summary>
public sealed class MemoryDriveProvider : KazoDriveProvider
{
    /// <inheritdoc />
    public override ValueTask<KazoDriveFolder> OpenAsync(KazoDriveOpenRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!request.Options.TryGetValue(MemoryDriveFeature.SpaceField, out string? space) || string.IsNullOrWhiteSpace(space))
        {
            throw new KazoDriveException(KazoDriveErrorKind.InvalidName, "The drive names no space.");
        }

        KazoDrivePath? root = null;
        if (request.Root is { } rootText && !KazoDrivePath.TryParse(rootText, out root))
        {
            throw new KazoDriveException(KazoDriveErrorKind.InvalidName, "The drive's root folder is not a valid path.");
        }

        KazoDriveFolder folder = new CloudDriveFolder(MemorySpaces.Get(space).Connect(request.Context), root);
        return ValueTask.FromResult(folder);
    }
}
