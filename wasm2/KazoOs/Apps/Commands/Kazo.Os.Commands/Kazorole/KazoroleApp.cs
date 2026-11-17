using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Commands.Kazorole;

/// <summary>
/// Manifest of "kazorole" (ADR 0247 §1): lists the roles the installed features declare and, under
/// <c>sudo</c>, grants or takes one back from an ordinary account. The build generates
/// <c>manifests/&lt;appId&gt;.json</c> from this class; it is never instantiated at runtime.
/// </summary>
// ADR 0070: stays on the main thread, like passwd: it uses the accounts administration and the OS's
// authentication window, which belong to the page.
[AppManifest(Execution = AppExecutionHost.MainThread)]
public sealed class KazoroleApp : AppDefinition
{
    /// <inheritdoc />
    public override string Id => "org.kazoos.commands.kazorole";

    /// <inheritdoc />
    public override string Name => "kazorole";

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Terminal;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(KazoroleCommand);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description => "List, grant and take back the roles of the features";

    /// <inheritdoc />
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;

    /// <inheritdoc />
    public override IReadOnlyList<string> Capabilities => [AppCapabilities.UsersManage];

    /// <inheritdoc />
    public override TerminalCommand? Terminal => new("kazorole", "kazorole list | grant <user> <role> | revoke <user> <role>");

    /// <inheritdoc />
    public override AppResourceProfileManifest Resources => new(
        BaselineCpuWeight: 0.0,
        BurstCpuWeight: 0.01,
        BaselineMemoryWeight: 0.0,
        BurstMemoryWeight: 0.01,
        BaselineStorageIoWeight: 0.0,
        BurstStorageIoWeight: 0.0,
        BaselineNetworkIoWeight: 0.0,
        BurstNetworkIoWeight: 0.0);
}
