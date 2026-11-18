using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Platform.Core.FileSystem.HostFolders;
using Kazo.Os.Simulation.Abstractions.Events;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
#if KAZOOS_CLOUD_DRIVES
using Kazo.Os.Platform.Core.CloudDrives;
#endif
#if KAZOOS_DROPBOX_DRIVES
using Kazo.Os.Platform.Core.CloudDrives.Dropbox;
#endif
#if KAZOOS_ONEDRIVE_DRIVES
using Kazo.Os.Platform.Core.CloudDrives.OneDrive;
#endif
#if KAZOOS_S3_DRIVES
using Kazo.Os.Platform.Core.CloudDrives.S3;
#endif

namespace Kazo.Os.Ecosystem;

/// <summary>
/// Composes the cloud drive connectors shipped with Kazo OS (ADR 0267 §6) as the build chose them
/// (ADR 0282 §5, <c>build/Kazo.Os.CloudDrives.targets</c>). A connector left out by its MSBuild
/// property is not compiled here; one switched off by its runtime switch is not registered; one
/// switched off by <c>KazoOs:CloudDrives:Disabled</c> or <c>/etc/kazoos/cloud-drives.config</c> is
/// registered behind a <see cref="SwitchableDriveSource"/> that hides it while the switch is off.
/// </summary>
internal static class CloudDriveServiceCollectionExtensions
{
    /// <summary>The Dropbox drive type.</summary>
    public const string DropboxType = "dropbox";

    /// <summary>The OneDrive drive type.</summary>
    public const string OneDriveType = "onedrive";

    /// <summary>The S3 drive type.</summary>
    public const string S3Type = "s3";

    /// <summary>Gets the shipped connectors compiled into this host and switched on by the build, in that order; read on each call.</summary>
    public static IReadOnlyList<string> CompiledTypes => ReadCompiledTypes();

    /// <summary>Gets the shipped types this host leaves out (build property or runtime switch).</summary>
    public static IReadOnlyList<string> LeftOutTypes =>
        [.. new[] { DropboxType, OneDriveType, S3Type }.Where(static type => !CompiledTypes.Contains(type))];

    /// <summary>
    /// Adds the host's settings with the types the build left out marked disabled, so a provider
    /// that has an app id but no connector is never offered (Settings ▸ Cloud accounts).
    /// </summary>
    /// <param name="settings">The host's <c>appsettings.json</c> values.</param>
    /// <returns>The same settings with <see cref="LeftOutTypes"/> added to <see cref="CloudProviderSettings.Disabled"/>.</returns>
    public static CloudProviderSettings WithLeftOutTypes(CloudProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings with { Disabled = [.. settings.Disabled.Union(LeftOutTypes, StringComparer.Ordinal)] };
    }

    /// <summary>Registers every compiled-in connector as an <see cref="INetworkDriveSource"/>.</summary>
    /// <param name="services">The OS services; <see cref="CloudAccountService"/> and <see cref="ICloudProviderCatalog"/> are registered.</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddKazoOsShippedCloudDrives(this IServiceCollection services) =>
        services.AddKazoOsShippedCloudDrives(CompiledTypes);

    /// <summary>Registers the connectors of <paramref name="types"/> that this build compiled in.</summary>
    /// <param name="services">The OS services.</param>
    /// <param name="types">The types to compose, normally <see cref="CompiledTypes"/>; tests pass a subset.</param>
    /// <returns><paramref name="services"/>.</returns>
    public static IServiceCollection AddKazoOsShippedCloudDrives(this IServiceCollection services, IReadOnlyCollection<string> types)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(types);
#if KAZOOS_DROPBOX_DRIVES
        // Offered only when KazoOs:CloudDrives (or /etc/kazoos/cloud-drives.config) has a Dropbox app key.
        if (types.Contains(DropboxType))
        {
            services.AddScoped<INetworkDriveSource>(provider => Switchable(provider, DropboxType, new DropboxDriveSource(
                provider.GetRequiredService<CloudAccountService>(),
                provider.GetRequiredService<ICloudProviderCatalog>(),
                new HttpClient(),
                provider.GetService<IHostActivitySignal>())));
        }
#endif
#if KAZOOS_ONEDRIVE_DRIVES
        // OneDrive drives over Microsoft Graph, offered when OneDrive has an app id.
        if (types.Contains(OneDriveType))
        {
            services.AddScoped<INetworkDriveSource>(provider => Switchable(provider, OneDriveType, new OneDriveDriveSource(
                provider.GetRequiredService<CloudAccountService>(),
                provider.GetRequiredService<ICloudProviderCatalog>(),
                new HttpClient(),
                provider.GetService<IHostActivitySignal>())));
        }
#endif
#if KAZOOS_S3_DRIVES
        // Buckets of S3 compatible services, signed with SigV4 in the OS; the service and the keys
        // are an s3 cloud account. No app registration, so only the switches hide it.
        if (types.Contains(S3Type))
        {
            services.AddScoped<INetworkDriveSource>(provider => Switchable(provider, S3Type, new S3DriveSource(
                provider.GetRequiredService<CloudAccountService>(),
                new HttpClient(),
                provider.GetService<IEventBus>(),
                provider.GetService<IHostActivitySignal>(),
                logger: provider.GetService<ILogger<S3DriveSource>>())));
        }
#endif
        return services;
    }

    private static string[] ReadCompiledTypes()
    {
        List<string> types = [];
#if KAZOOS_DROPBOX_DRIVES
        if (CloudDriveFeatures.IsDropboxSupported)
        {
            types.Add(DropboxType);
        }
#endif
#if KAZOOS_ONEDRIVE_DRIVES
        if (CloudDriveFeatures.IsOneDriveSupported)
        {
            types.Add(OneDriveType);
        }
#endif
#if KAZOOS_S3_DRIVES
        if (CloudDriveFeatures.IsS3Supported)
        {
            types.Add(S3Type);
        }
#endif
        return [.. types];
    }

    /// <summary>Wraps a connector so the runtime switch of its type hides it (ADR 0282 §5).</summary>
    private static SwitchableDriveSource Switchable(IServiceProvider provider, string type, INetworkDriveSource source)
    {
        ICloudProviderCatalog catalog = provider.GetRequiredService<ICloudProviderCatalog>();
        return new SwitchableDriveSource(
            source,
            cancellationToken => catalog is CloudProviderCatalog switches
                ? switches.IsTypeEnabledAsync(type, cancellationToken)
                : ValueTask.FromResult(true));
    }
}
