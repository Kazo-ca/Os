namespace Kazo.Os.App.Abstractions;

/// <summary>
/// The C# source of an optional feature of the OS (ADR 0243): an <see cref="AppDefinition"/> of kind
/// <see cref="AppKind.Feature"/> that has no entry point of its own but a list of typed children,
/// <see cref="EntryPoints"/>. The manifest generator writes the feature's <c>manifest.json</c> and
/// one <c>entrypoints/&lt;suffix&gt;.json</c> per child; the OS installs, approves, activates and
/// removes them as one unit, and runs each child as an app of its kind.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="AppDefinition.Capabilities"/> is the <b>set</b> of the feature (ADR 0243 §3): what the
/// Store, the approval window and <c>kazopkg</c> show once, at installation. Each child declares its
/// own subset in <see cref="FeatureEntryPoint.Capabilities"/>; a capability outside the set fails
/// the build (<c>manifest.feature.capability-outside-set</c>).
/// </para>
/// <para>
/// What a stand-alone app declares for itself (a command, a window, file handlers, components,
/// a site) is sealed to its default here: a feature registers no association (D64) and nothing of
/// its own runs. The children carry those declarations.
/// </para>
/// </remarks>
public abstract class FeatureDefinition : AppDefinition
{
    /// <summary>Always <see cref="AppKind.Feature"/>.</summary>
    public sealed override AppKind Kind => AppKind.Feature;

    /// <summary>
    /// A feature has no entry point of its own (ADR 0243 §1): this member is never read. A feature
    /// class that overrides it fails the build with <c>KOSAPP0032</c>.
    /// </summary>
    public override Type EntryPoint => null!;

    /// <summary>A feature has no mobile entry point of its own.</summary>
    public sealed override Type? MobileEntryPoint => null;

    /// <summary>
    /// Gets the children of the feature (ADR 0243 §2): one app of an ordinary kind per entry, with
    /// the identifier <c>&lt;feature id&gt;.&lt;suffix&gt;</c>.
    /// </summary>
    public abstract IReadOnlyList<FeatureEntryPoint> EntryPoints { get; }

    /// <summary>A feature has no command of its own: a child of kind <see cref="AppKind.Terminal"/> does.</summary>
    public sealed override TerminalCommand? Terminal => null;

    /// <summary>A feature registers no file association (D64).</summary>
    public sealed override IReadOnlyList<FileHandler> FileHandlers => [];

    /// <summary>A feature registers no embeddable component.</summary>
    public sealed override IReadOnlyList<AppComponent> Components => [];

    /// <summary>A feature has no window of its own.</summary>
    public sealed override WindowOptions? Window => null;

    /// <summary>A feature answers on no URL.</summary>
    public sealed override IReadOnlyList<string> Urls => [];

    /// <summary>A feature has no Blazor root.</summary>
    public sealed override Type? BlazorRoot => null;

    /// <summary>A feature is not a window.</summary>
    public sealed override bool SingleInstancePerUser => false;

    /// <summary>A feature is not a service: a child of kind <see cref="AppKind.Service"/> starts on its own.</summary>
    public sealed override bool AutoStart => false;

    /// <summary>A feature is not a service.</summary>
    public sealed override AppServiceStartup? ServiceStartup => null;

    /// <summary>A feature is not a window.</summary>
    public sealed override bool SupportsBack => false;

    /// <summary>A feature is not launched: it is hidden from launchers; its children choose for themselves.</summary>
    public override AppLaunchVisibility LaunchVisibility => AppLaunchVisibility.Hidden;
}

/// <summary>
/// One child of a <see cref="FeatureDefinition"/> (ADR 0243 §2): an app of an ordinary kind the
/// feature brings, identified by <c>&lt;feature id&gt;.&lt;suffix&gt;</c>.
/// </summary>
/// <param name="Suffix">The last segment of the child's id (<c>cli</c> for <c>ca.kazo.x86.cli</c>): lowercase letters, digits and dashes.</param>
/// <param name="Name">The child's display name, as the task manager and <c>ps</c> show it.</param>
/// <param name="Kind">The child's kind: <see cref="AppKind.Window"/>, <see cref="AppKind.Terminal"/> or <see cref="AppKind.Service"/>.</param>
/// <param name="EntryPoint">The child's entry-point type, in the feature's assembly: a window component, a command or a service.</param>
public sealed record FeatureEntryPoint(string Suffix, string Name, AppKind Kind, Type EntryPoint)
{
    /// <summary>Gets the child's description; defaults to <see cref="Name"/>.</summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the child's subset of the feature's capabilities (ADR 0243 §3): what its process holds
    /// at run time. Empty by default; a capability outside the feature's set fails the build.
    /// </summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Gets the shell command of a <see cref="AppKind.Terminal"/> child; required for that kind.</summary>
    public TerminalCommand? Terminal { get; init; }

    /// <summary>Gets the window sizes of a <see cref="AppKind.Window"/> child, or <see langword="null"/> for the defaults.</summary>
    public WindowOptions? Window { get; init; }

    /// <summary>Gets the mobile entry-point type of a window child, or <see langword="null"/> for a desktop-only one.</summary>
    public Type? MobileEntryPoint { get; init; }

    /// <summary>Gets the launcher category; defaults to the feature's.</summary>
    public string? Category { get; init; }

    /// <summary>
    /// Gets whether shell surfaces offer the child for direct launch. Hidden by default: a feature's
    /// children are reached through their command, their service or another app, unless one is a
    /// window the user opens by itself.
    /// </summary>
    public AppLaunchVisibility LaunchVisibility { get; init; } = AppLaunchVisibility.Hidden;

    /// <summary>Gets package-relative icon asset paths, declared in the feature's <see cref="AppDefinition.Assets"/>.</summary>
    public IReadOnlyList<string> Icons { get; init; } = [];

    /// <summary>Gets whether a second launch of a window child focuses the running instance.</summary>
    public bool SingleInstancePerUser { get; init; }

    /// <summary>Gets whether a service child starts on its own at login.</summary>
    public bool AutoStart { get; init; }

    /// <summary>Gets when an automatic service child starts; <see cref="AppServiceStartup.Boot"/> is honored for system apps only.</summary>
    public AppServiceStartup? ServiceStartup { get; init; }

    /// <summary>Gets whether a window child takes part in platform Back navigation.</summary>
    public bool SupportsBack { get; init; }

    /// <summary>
    /// Gets where the OS runs the child (ADR 0070). <see langword="null"/> (the default) means
    /// <see cref="AppExecutionHost.MainThread"/>, whatever the kind: a feature is installed from a
    /// package, and an installed app runs on the page (ADR 0182 §6).
    /// </summary>
    public AppExecutionHost? Execution { get; init; }

    /// <summary>Gets the child's resource weights, or <see langword="null"/> for the feature's.</summary>
    public AppResourceProfileManifest? Resources { get; init; }

    /// <summary>Gets the settings schema the child persists, or <see langword="null"/> when it persists none.</summary>
    public AppSettingsSchemaManifest? Settings { get; init; }

    /// <summary>Gets the custom intents the child receives.</summary>
    public IReadOnlyList<AppIntentDeclarationManifest> Intents { get; init; } = [];
}

/// <summary>What <see cref="AppDefinitionMapper.ToFeatureManifests(FeatureDefinition, string)"/> produces from one <see cref="FeatureDefinition"/>.</summary>
/// <param name="Feature">The feature's own manifest (kind <see cref="AppKind.Feature"/>, no entry point).</param>
/// <param name="Children">One manifest per <see cref="FeatureDefinition.EntryPoints"/> entry, in declaration order, each naming the feature as its parent.</param>
public sealed record FeatureManifests(AppManifest Feature, IReadOnlyList<AppManifest> Children)
{
    /// <summary>Gets the feature manifest followed by its children.</summary>
    public IEnumerable<AppManifest> All => Children.Prepend(Feature);
}
