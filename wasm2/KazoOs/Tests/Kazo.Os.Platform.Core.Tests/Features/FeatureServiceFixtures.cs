using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Features;
using Kazo.Os.Tests.Support;

namespace Kazo.Os.Platform.Core.Tests.Features;

/// <summary>A feature of fixture that provides <see cref="IFixtureMachine"/> (ADR 0262 §1).</summary>
public sealed class MachineFeature : FeatureDefinition
{
    public override string Id => FeatureServiceFixtures.FeatureId;

    public override string Name => "Machine";

    public override string Version => FeatureServiceFixtures.FeatureVersion;

    public override IReadOnlyList<FeatureEntryPoint> EntryPoints => [];

    public override IReadOnlyList<FeatureService> ProvidedServices => [new(typeof(IFixtureMachine), typeof(MachineService))];
}

/// <summary>The fixture's service: it names the feature and the app that calls it.</summary>
public sealed class MachineService : IFixtureMachine
{
    public MachineService(IFeatureServiceContext context)
    {
        Context = context;
        Interlocked.Increment(ref Created);
    }

    /// <summary>Gets how many instances were created in the test process.</summary>
    public static int Created;

    /// <summary>Gets the context the service received.</summary>
    public IFeatureServiceContext Context { get; }

    public string Describe() => $"machine of {Context.Feature.Manifest.Id} for {Context.Caller?.AppId ?? "nobody"}";

    // ADR 0262 §2: a missing permission is an exception of the operation, never a null at resolution.
    public void Start() => throw new UnauthorizedAccessException($"{Context.Caller?.AppId} may not start a machine.");
}

/// <summary>A fixture service whose constructor fails the first time (ADR 0262 §4).</summary>
public sealed class FailingOnceService : IFixtureMachine
{
    public static bool Fail = true;

    public FailingOnceService()
    {
        if (Fail)
        {
            Fail = false;
            throw new InvalidOperationException("not ready");
        }
    }

    public string Describe() => "ready";

    public void Start()
    {
    }
}

/// <summary>An app that asks for the machine, with a dependency on the feature.</summary>
public sealed class ClientApp(params AppDependencyManifest[] dependencies) : AppDefinition
{
    public override string Id => FeatureServiceFixtures.ClientId;

    public override string Name => "Client";

    public override AppKind Kind => AppKind.Window;

    public override Type EntryPoint => typeof(ClientApp);

    public override IReadOnlyList<AppDependencyManifest> Dependencies => dependencies;
}

/// <summary>Builds the manifests of the tests of ADR 0262.</summary>
public static class FeatureServiceFixtures
{
    public const string FeatureId = "org.test.machine";

    public const string FeatureVersion = "1.2.0";

    public const string ClientId = "org.test.client";

    public static AppManifest Feature() =>
        AppDefinitionMapper.ToFeatureManifests(new MachineFeature(), KazoOsSdk.Version.ToString()).Feature;

    public static AppManifest Client(string minimum = "1.0.0", string? maximum = null, bool optional = true) =>
        AppDefinitionMapper.ToManifest(new ClientApp(new AppDependencyManifest(FeatureId, minimum, maximum, optional)));

    public static AppManifest ClientWithoutDependency() => AppDefinitionMapper.ToManifest(new ClientApp());
}
