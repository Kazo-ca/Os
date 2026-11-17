using System.Globalization;
using Kazo.Os.AppSdk.IO;
using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.FileSystem.HostFolders;

/// <summary>
/// A drive source that says how the user connects it (ADR 0282 §5): its name, its icon, its
/// connection and, for a type a feature provides, that feature. The Explorer, Settings and
/// <c>mount -t</c> read it through <see cref="KazoDriveInfo.DescribeNetworkDriveTypesAsync"/> and
/// never name a service themselves.
/// </summary>
public interface IDescribedNetworkDriveSource : INetworkDriveSource
{
    /// <summary>Describes the type.</summary>
    /// <param name="language">The language of the texts the OS writes (labels of its own forms); a feature's texts stay as it wrote them.</param>
    /// <returns>The description. The scopes of an OAuth type are filled from the configured providers by the caller.</returns>
    KazoNetworkDriveTypeInfo Describe(CultureInfo language);
}

/// <summary>Icons of the OS's own network drive types, as <see cref="KazoNetworkDriveTypeInfo.Icon"/> names them.</summary>
public static class NetworkDriveTypeIcons
{
    /// <summary>A folder of this computer.</summary>
    public const string HostFolder = "lucide:hard-drive";

    /// <summary>A storage with no icon of its own.</summary>
    public const string Generic = "lucide:cloud";
}
