using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Craft;

namespace Kazo.Os.Fixtures.CraftApp;

/// <summary>
/// Manifest of the Craft test package of ADR 0259: a window app that shows a fake Craft app in an
/// iframe through the <c>Kazo.Os.AppSdk.Craft</c> module served by the OS. Its files (iframe page,
/// fake build) are not declared: <c>KazoOsPack</c> adds every <c>wwwroot/</c> file (ADR 0221).
/// </summary>
[AppManifest]
public sealed class CraftFixtureApp : AppDefinition
{
    /// <summary>The app's id, also the folder of its per-user settings (<c>~/.config/&lt;id&gt;/</c>).</summary>
    public const string AppId = "org.kazoos.fixtures.craft";

    /// <summary>The product name, in the window title.</summary>
    public const string AppName = "Craft Fixture";

    /// <summary>The fake Craft app's only formats: its document (<c>.fake</c>) and a text copy (<c>.txt</c>).</summary>
    public static CraftDocumentFormats Formats { get; } = new(
    [
        new CraftDocumentFormat(".fake", "application/x-kazoos-fake", IsDefault: true, IsCopy: false),
        new CraftDocumentFormat(".txt", "text/plain", IsDefault: false, IsCopy: true, Opens: false),
    ]);

    /// <inheritdoc />
    public override string Id => AppId;

    /// <inheritdoc />
    public override string Name => AppName;

    /// <inheritdoc />
    public override AppKind Kind => AppKind.Window;

    /// <inheritdoc />
    public override Type EntryPoint => typeof(CraftFixtureWindow);

    /// <inheritdoc />
    public override string PublisherId => "pub.kazoos";

    /// <inheritdoc />
    public override string Description => "Test package: a Craft app window on the Kazo.Os.AppSdk.Craft module, around a fake Craft app.";

    /// <inheritdoc />
    /// <remarks>What a Craft app window needs: the OS file dialogs, the user's files, its settings folder, another window of itself.</remarks>
    public override IReadOnlyList<string> Capabilities =>
    [
        AppCapabilities.DialogFileOpen,
        AppCapabilities.DialogFileSave,
        AppCapabilities.FileSystemUserHomeRead,
        AppCapabilities.FileSystemUserHomeWrite,
        AppCapabilities.FileSystemPrivateRead,
        AppCapabilities.FileSystemPrivateWrite,
        AppCapabilities.AppsLaunch,
    ];

    /// <inheritdoc />
    public override IReadOnlyList<FileHandler> FileHandlers => Formats.FileHandlers;
}
