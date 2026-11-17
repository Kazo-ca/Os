using System.Diagnostics.CodeAnalysis;

namespace Kazo.Os.Platform.Core.CloudDrives;

/// <summary>
/// The build-time switches of ADR 0282, section 5, as seen at run time. Each MSBuild property
/// (<c>KazoOsDropboxDrives</c>, <c>KazoOsOneDriveDrives</c>, <c>KazoOsS3Drives</c>) becomes a
/// <c>RuntimeHostConfigurationOption</c> through <c>build/Kazo.Os.CloudDrives.targets</c>.
/// </summary>
/// <remarks>
/// The guards are marked <see cref="FeatureSwitchDefinitionAttribute"/>, like the resource accounting
/// switches of ADR 0066. ADR 0113 keeps every Kazo OS assembly whole, so a connector switched off
/// while another one keeps this assembly stays in the binary but is never composed; with all three
/// switched off, the build leaves the assembly out entirely. A switch that is absent (unit tests, a
/// host that does not import the targets) reads as <see langword="true"/>: the connector is present.
/// The types stay in the <c>Kazo.Os.Platform.Core.CloudDrives</c> namespace they had in Platform
/// Core, so a host that composed them only adds this package.
/// </remarks>
public static class CloudDriveFeatures
{
    /// <summary>Runtime configuration name of the Dropbox switch.</summary>
    public const string DropboxSwitchName = "Kazo.Os.CloudDrives.Dropbox.IsSupported";

    /// <summary>Runtime configuration name of the OneDrive switch.</summary>
    public const string OneDriveSwitchName = "Kazo.Os.CloudDrives.OneDrive.IsSupported";

    /// <summary>Runtime configuration name of the S3 switch.</summary>
    public const string S3SwitchName = "Kazo.Os.CloudDrives.S3.IsSupported";

    /// <summary>Gets whether the Dropbox connector is compiled into this host.</summary>
    [FeatureSwitchDefinition(DropboxSwitchName)]
    public static bool IsDropboxSupported =>
        !AppContext.TryGetSwitch(DropboxSwitchName, out bool supported) || supported;

    /// <summary>Gets whether the OneDrive connector is compiled into this host.</summary>
    [FeatureSwitchDefinition(OneDriveSwitchName)]
    public static bool IsOneDriveSupported =>
        !AppContext.TryGetSwitch(OneDriveSwitchName, out bool supported) || supported;

    /// <summary>Gets whether the S3 connector is compiled into this host.</summary>
    [FeatureSwitchDefinition(S3SwitchName)]
    public static bool IsS3Supported =>
        !AppContext.TryGetSwitch(S3SwitchName, out bool supported) || supported;
}
