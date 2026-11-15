using ManifestGenerator.Fixtures.WithLibrary.Contracts;

namespace ManifestGenerator.Fixtures.WithLibrary.Protocol;

/// <summary>Fixture implementation, in a library private to the app.</summary>
public sealed class FixtureProtocol : IFixtureContract
{
    /// <inheritdoc />
    public string Describe() => "protocol ready";
}
