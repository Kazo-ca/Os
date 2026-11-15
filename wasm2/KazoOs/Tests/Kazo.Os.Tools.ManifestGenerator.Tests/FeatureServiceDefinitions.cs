using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Features;

namespace Kazo.Os.Tools.ManifestGenerator.Tests;

/// <summary>The implementation of a valid service (ADR 0262 §1): its contract is in the core library.</summary>
public sealed class FixtureServiceProvider(IFeatureServiceContext context) : IServiceProvider
{
    public IFeatureServiceContext Context { get; } = context;

    public object? GetService(Type serviceType) => null;
}

/// <summary>A class that does not implement the contract it is declared for.</summary>
public sealed class NotAProvider;

/// <summary>A provider the OS cannot create: its constructor asks for something else.</summary>
public sealed class UncreatableProvider(string name) : IServiceProvider
{
    public string Name { get; } = name;

    public object? GetService(Type serviceType) => null;
}

/// <summary>A feature that provides one service, run as its daemon (ADR 0262 §1).</summary>
public sealed class ProvidingFeature : ValidFeature
{
    public override IReadOnlyList<FeatureService> ProvidedServices => [new(typeof(IServiceProvider), typeof(FixtureServiceProvider))];

    public override string? ServiceEntryPoint => "daemon";
}

/// <summary>A feature whose implementation does not implement its contract (<c>KOSAPP0035</c>).</summary>
public sealed class FeatureWithWrongProvider : ValidFeature
{
    public override IReadOnlyList<FeatureService> ProvidedServices => [new(typeof(IServiceProvider), typeof(NotAProvider))];
}

/// <summary>A feature whose implementation has no constructor the OS can call (<c>KOSAPP0035</c>).</summary>
public sealed class FeatureWithUncreatableProvider : ValidFeature
{
    public override IReadOnlyList<FeatureService> ProvidedServices => [new(typeof(IServiceProvider), typeof(UncreatableProvider))];
}

/// <summary>A feature whose contract lives in its own assembly (<c>KOSAPP0035</c>).</summary>
public sealed class FeatureWithOwnContract : ValidFeature
{
    public override IReadOnlyList<FeatureService> ProvidedServices => [new(typeof(IOwnContract), typeof(OwnProvider))];
}

/// <summary>A contract in the feature's own assembly.</summary>
public interface IOwnContract;

/// <summary>Its implementation.</summary>
public sealed class OwnProvider : IOwnContract;

/// <summary>A feature whose service entry point is not one of its children (<c>KOSAPP0035</c>).</summary>
public sealed class FeatureWithUnknownServiceEntryPoint : ValidFeature
{
    public override IReadOnlyList<FeatureService> ProvidedServices => [new(typeof(IServiceProvider), typeof(FixtureServiceProvider))];

    public override string? ServiceEntryPoint => "nowhere";
}

/// <summary>Code that asks for a feature's service, so this assembly refers to <see cref="KazoFeatures"/> (<c>KOSAPP0034</c>).</summary>
public static class FeatureClientCode
{
    public static bool HasProvider() => KazoFeatures.IsAvailable<IServiceProvider>();
}
