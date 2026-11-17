using System.Reflection;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Drives;
using Kazo.Os.AppSdk.Features;
using Kazo.Os.Platform.Core.CloudAccounts;
using Kazo.Os.Platform.Core.FileSystem.DriveProviders;
using Kazo.Os.Platform.Core.Lifecycle;
using Kazo.Os.Platform.Core.Tests.CloudDrives;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.Platform.Core.Tests.FileSystem.DriveProviders;

/// <summary>A feature that provides the <c>memdrive</c> type with <see cref="InMemoryDriveProvider"/> (ADR 0282 §1).</summary>
public sealed class MemoryDrivesFeature(string id = DriveProviderFixtures.FeatureId, string version = "1.0.0", Type? provider = null) : FeatureDefinition
{
    public override string Id => id;

    public override string Name => "Memory drives";

    public override string Version => version;

    public override string PublisherId => "org.test";

    public override IReadOnlyList<string> Capabilities => [AppCapabilities.DrivesProvide];

    public override IReadOnlyList<FeatureEntryPoint> EntryPoints => [];

    public override IReadOnlyList<KazoDriveProviderDeclaration> DriveProviders =>
    [
        new(DriveProviderFixtures.Type, "Memory", provider ?? typeof(InMemoryDriveProvider), AppDriveConnectionManifest.WithForm(
            new AppDriveFormFieldManifest(InMemoryDriveProvider.ServiceOption, "Service", AppDriveFormFieldKind.Text))),
    ];
}

/// <summary>A provider whose constructor throws, as a broken third-party package would.</summary>
public sealed class ThrowingDriveProvider : KazoDriveProvider
{
    public ThrowingDriveProvider() => throw new InvalidOperationException("the provider is broken");

    public override ValueTask<KazoDriveFolder> OpenAsync(KazoDriveOpenRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

/// <summary>A provider that receives its feature's context, to check it is created in it (ADR 0282 §3).</summary>
public sealed class ContextDriveProvider(IFeatureServiceContext context) : KazoDriveProvider
{
    public static IFeatureServiceContext? LastContext;

    private readonly InMemoryDriveProvider _inner = Remember(context);

    public override ValueTask<KazoDriveFolder> OpenAsync(KazoDriveOpenRequest request, CancellationToken cancellationToken = default) =>
        _inner.OpenAsync(request, cancellationToken);

    private static InMemoryDriveProvider Remember(IFeatureServiceContext context)
    {
        LastContext = context;
        return new InMemoryDriveProvider();
    }
}

/// <summary>Builds the pieces of ADR 0282's work item 4 for tests.</summary>
public static class DriveProviderFixtures
{
    public const string FeatureId = "org.test.memdrives";

    public const string Type = "memdrive";

    public static AppManifest Feature(string id = FeatureId, string version = "1.0.0", Type? provider = null) =>
        AppDefinitionMapper.ToFeatureManifests(new MemoryDrivesFeature(id, version, provider), KazoOsSdk.Version.ToString()).Feature;

    /// <summary>The context the OS lends a provider, over the test accounts (as work item 5 does over the real ones).</summary>
    public static FeatureDriveContextFactory Contexts(FakeCloudAccountTokens tokens, Func<string, InMemoryDriveService?> serviceOf) =>
        (type, accountId, availabilityChanged, _) =>
        {
            Dictionary<string, string> options = new(StringComparer.Ordinal);
            if (serviceOf(accountId) is { } service)
            {
                options[InMemoryDriveProvider.ServiceOption] = service.Key;
            }

            return ValueTask.FromResult(new FeatureDriveConnection(new TokensContext(tokens, type, accountId, availabilityChanged), options));
        };

    /// <summary>One page: a live catalog, the enablement registry, the loader and the sources it gives.</summary>
    public sealed class Page : IDisposable
    {
        public Page(FakeCloudAccountTokens tokens, Func<string, InMemoryDriveService?> serviceOf, params AppManifest[] manifests)
            : this(tokens, serviceOf, FeatureDriveSource.DefaultOpenTimeout, manifests)
        {
        }

        public Page(FakeCloudAccountTokens tokens, Func<string, InMemoryDriveService?> serviceOf, TimeSpan openTimeout, params AppManifest[] manifests)
        {
            Catalog = Build(manifests).CreateLiveCopy();
            Enablement = new AppEnablementRegistry(Catalog);
            Loader = new FeatureDriveProviderLoader(
                (assembly, _) =>
                {
                    Loads.Add(assembly);
                    // The feature's assembly, by file name, among the ones the tests already loaded.
                    return ValueTask.FromResult(AssemblyFails ? null : AppDomain.CurrentDomain.GetAssemblies()
                        .FirstOrDefault(loaded => string.Equals(loaded.GetName().Name + ".dll", assembly, StringComparison.Ordinal)));
                },
                (runAs, implementation) =>
                {
                    Started.Add((runAs.Id, implementation));
                    return ProcessFails ? null : new MinimalAppExecutionContext(runAs, userId: "kazoos-svc");
                },
                (feature, message) =>
                {
                    lock (FeatureLog)
                    {
                        FeatureLog.Add((feature, message));
                    }
                });
            Sources = new FeatureDriveSources(Catalog, Enablement, registration => new FeatureDriveSource(
                registration, Loader, tokens, Contexts(tokens, serviceOf))
            {
                OpenTimeout = openTimeout,
                SaveFormAccount = (type, displayName, options, secrets, _) =>
                {
                    FormAccounts.Add((type, displayName, options, secrets));
                    CloudAccount account = tokens.Add($"form-{FormAccounts.Count}", type, displayName: displayName);
                    return ValueTask.FromResult(account);
                }
            });
        }

        public AppCatalog Catalog { get; }

        public AppEnablementRegistry Enablement { get; }

        public FeatureDriveProviderLoader Loader { get; }

        public FeatureDriveSources Sources { get; }

        public bool AssemblyFails { get; set; }

        public bool ProcessFails { get; set; }

        public List<string> Loads { get; } = [];

        /// <summary>The form accounts the source saved: type, name, plain values and secrets (ADR 0282 §3).</summary>
        public List<(string Type, string Name, IReadOnlyDictionary<string, string> Options, IReadOnlyDictionary<string, string> Secrets)> FormAccounts { get; } = [];

        public List<(string AppId, Type Implementation)> Started { get; } = [];

        /// <summary>Gets the lines written to the features' own logs, <c>/var/log/apps/&lt;feature&gt;.log</c>.</summary>
        public List<(string Feature, string Message)> FeatureLog { get; } = [];

        /// <summary>Gets the one source of the page.</summary>
        public FeatureDriveSource Source => Assert.IsType<FeatureDriveSource>(Assert.Single(Sources.GetSources()));

        public void Install(params AppManifest[] manifests) => Catalog.ReplaceContents(Build(manifests));

        public void Dispose() => Sources.Dispose();

        private static AppCatalog Build(IEnumerable<AppManifest> manifests)
        {
            AppCatalogBuildResult built = AppCatalog.Build(manifests);
            Assert.True(built.IsSuccess, string.Join(", ", built.Errors.Select(static error => error.Message)));
            return built.Catalog!;
        }
    }

    /// <summary>
    /// The context of one drive over <see cref="FakeCloudAccountTokens"/>: an account that is gone or
    /// must sign in again is <see cref="KazoDriveErrorKind.SignInRequired"/>, a provider out of reach
    /// is <see cref="KazoDriveErrorKind.Unavailable"/> (the contract of work item 5).
    /// </summary>
    private sealed class TokensContext(FakeCloudAccountTokens tokens, string type, string accountId, Action availabilityChanged) : IKazoDriveProviderContext
    {
        public string Type => type;

        public KazoDriveAccount? Account => null;

        public async ValueTask<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                return await tokens.GetAccessTokenAsync(accountId, cancellationToken);
            }
            catch (CloudAccountException exception) when (exception.Error is CloudAccountError.ProviderUnavailable)
            {
                throw new KazoDriveException(KazoDriveErrorKind.Unavailable, exception.Message, exception);
            }
            catch (CloudAccountException exception)
            {
                throw new KazoDriveException(KazoDriveErrorKind.SignInRequired, exception.Message, exception);
            }
        }

        public ValueTask<string?> GetSecretAsync(string name, CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);

        public void NotifyAvailabilityChanged() => availabilityChanged();
    }
}
