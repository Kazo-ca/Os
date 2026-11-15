using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using ManifestGenerator.Fixtures.WithLibrary.Contracts;
using ManifestGenerator.Fixtures.WithLibrary.Protocol;

namespace ManifestGenerator.Fixtures.WithLibrary;

/// <summary>Fixture command: its code calls the two libraries of the app.</summary>
public sealed class LibraryCommand(AppManifest manifest) : TerminalAppBase(manifest)
{
    /// <inheritdoc />
    public override async ValueTask<int> ExecuteAsync(TerminalExecutionContext context, CancellationToken cancellationToken)
    {
        IFixtureContract contract = new FixtureProtocol();
        await context.StandardOutput.WriteLineAsync(contract.Describe());
        return 0;
    }
}

/// <summary>Fixture definition: one app whose project references two libraries of its own.</summary>
[AppManifest]
public sealed class LibraryApp : AppDefinition
{
    public override string Id => "org.kazoos.fixtures.with-library";
    public override string Name => "with-library";
    public override AppKind Kind => AppKind.Terminal;
    public override Type EntryPoint => typeof(LibraryCommand);
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;
    public override TerminalCommand? Terminal => new("with-library", "with-library");
}
