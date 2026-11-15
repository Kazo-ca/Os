using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.AppSdk.Blazor.Files;
using Kazo.Os.Simulation.Abstractions.FileSystem;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.FileExplorer;

/// <summary>
/// The File Explorer's provider of <c>kazoos.file-dialogs</c> (ADR 0148 §6, ADR 0155 §5). The OS
/// hands it the dialog's name, its request record and a <see cref="Completed"/> callback, and shows it in
/// a window modal to the requesting app's window; this component answers once with the result record.
/// </summary>
public sealed partial class ExplorerFileDialogs
{
    private readonly string _headingId = $"explorer-file-dialog-{Guid.NewGuid():N}";
    private IStringLocalizer<Strings>? _localizer;
    private bool _completed;

    // "Open with"
    private IReadOnlyList<KazoAppInfo>? _apps;
    private string? _selectedAppId;
    private string? _extension;
    private bool _alwaysUse;
    private IReadOnlyList<KazoAppInfo> _installed = [];
    private string? _openWithPath;

    // "Open with": the search box and the category selector over the installed apps
    private readonly string _searchId = $"explorer-open-with-search-{Guid.NewGuid():N}";
    private readonly string _categoryId = $"explorer-open-with-category-{Guid.NewGuid():N}";
    private string _search = string.Empty;
    private string _category = string.Empty;
    private IReadOnlyList<string> _categories = [];

    // "Open with": the Store apps that open the file (ADR 0257 §4)
    private readonly string _storeHeadingId = $"explorer-store-apps-{Guid.NewGuid():N}";
    private readonly HashSet<string> _brokenIcons = new(StringComparer.Ordinal);
    private IReadOnlyList<AppIntentSuggestedCatalogApp>? _storeApps;
    private string? _storeError;

    // Properties
    private FileSystemEntryMetadata? _metadata;
    private string? _statError;
    private FileSystemTreeSize? _folderSize;
    private bool _folderSizeFailed;

    // Conflict
    private bool _applyToAll;

    /// <summary>Gets or sets which dialog to show (<see cref="KazoFileDialogs"/>).</summary>
    [Parameter]
    public string? Dialog { get; set; }

    /// <summary>Gets or sets the dialog's request record (<see cref="OpenWithDialogRequest"/>, …).</summary>
    [Parameter]
    public object? Request { get; set; }

    /// <summary>Gets or sets the callback invoked once with the result record, or <see langword="null"/> to cancel.</summary>
    [Parameter]
    public EventCallback<object?> Completed { get; set; }

    /// <summary>The explorer's texts in the language of the app that asked for the dialog (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= HostContext.GetLocalizer<Strings>();

    private CultureInfo FormatCulture => HostContext.FormatCulture;

    // "Select an installed app to open {0}:" split around the bold path.
    private (string Before, string After) OpenWithPrompt => Strings.Around(L["OpenWith.Prompt"]);

    // "Moving {0}" split around the source path.
    private (string Before, string After) MovingText => Strings.Around(L["Conflict.Moving"]);

    /// <summary>
    /// Only an app that declares the extension for "open" can become its default (ADR 0121 §4): the OS
    /// refuses the others, so the box is offered for them alone.
    /// </summary>
    private bool CanRemember => _extension is not null
        && _apps?.FirstOrDefault(app => app.Id == _selectedAppId) is { } selected
        && selected.FileHandlers.Any(handler =>
            handler.Extensions.Any(extension => string.Equals(extension, _extension, StringComparison.OrdinalIgnoreCase))
            && handler.Actions.Any(action => string.Equals(action, "open", StringComparison.OrdinalIgnoreCase)));

    /// <inheritdoc />
    protected override async Task OnInitializedAsync()
    {
        switch (Request)
        {
            case OpenWithDialogRequest openWith:
                await LoadAppsAsync(openWith);
                break;
            case FilePropertiesDialogRequest properties:
                await LoadMetadataAsync(properties.Path);
                break;
        }
    }

    /// <summary>
    /// The installed window apps (ADR 0048: the catalog hides the others), or only the candidates the OS
    /// named, in their order.
    /// </summary>
    private async Task LoadAppsAsync(OpenWithDialogRequest request)
    {
        _extension = ExplorerFileView.ExtensionOf(request.Path);
        IReadOnlyList<KazoAppInfo> installed;
        try
        {
            installed = await HostContext.Catalog.GetInstalledAsync(HostContext.CancellationToken);
        }
        catch (Exception exception) when (exception is AppGatewayAccessDeniedException or NotSupportedException)
        {
            installed = [];
        }

        _installed = installed;
        _openWithPath = request.Path;
        _storeApps = await LoadStoreAppsAsync(request.Path);
        _apps = request.CandidateAppIds is { } candidates
            ? [.. candidates.Select(id => installed.FirstOrDefault(app => app.Id == id)).OfType<KazoAppInfo>()]
            : [.. installed.Where(app => app.Kind == AppKind.Window && !app.IsHidden).OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)];
        _categories = OpenWithAppFilter.Categories(_apps, CategoryLabel);
        _selectedAppId = _apps.FirstOrDefault()?.Id;
    }

    /// <summary>The apps the list shows: those of <see cref="_apps"/> that match the search and the category.</summary>
    private IReadOnlyList<KazoAppInfo> VisibleApps => _apps is null ? [] : OpenWithAppFilter.Apply(_apps, _search, _category);

    /// <summary>The search box and the category selector are offered once there is more than one app to tell apart.</summary>
    private bool ShowFilters => _apps is { Count: > 1 };

    /// <summary>
    /// After the search or the category changes: a selected app the filter hides is no longer the one
    /// « Open » would open, so the first app still listed becomes the selection (none when nothing matches).
    /// </summary>
    private void OnFilterChanged()
    {
        IReadOnlyList<KazoAppInfo> visible = VisibleApps;
        if (!visible.Any(app => app.Id == _selectedAppId))
        {
            _selectedAppId = visible.FirstOrDefault()?.Id;
        }
    }

    /// <summary>
    /// The shown name of a manifest category: the usual ids are translated (<c>Category.Utilities</c>…),
    /// any other id is shown title-cased, as the Start menu does.
    /// </summary>
    private string CategoryLabel(string category)
    {
        string titled = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(category.Replace('-', ' '));
        LocalizedString text = L["Category." + titled.Replace(" ", string.Empty, StringComparison.Ordinal)];
        return text.ResourceNotFound ? titled : text.Value;
    }

    /// <summary>
    /// The Store apps that open the file (ADR 0257 §4), from the catalogs the OS already downloaded:
    /// never waits for the network. A requesting app without <c>apps.launch</c>, an unreadable path or
    /// an OS without the search shows none: the box stays as it was.
    /// </summary>
    private async Task<IReadOnlyList<AppIntentSuggestedCatalogApp>> LoadStoreAppsAsync(string path)
    {
        try
        {
            return await HostContext.Intents.SuggestCatalogAppsAsync(VirtualPath.Parse(path), cancellationToken: HostContext.CancellationToken);
        }
        catch (Exception exception) when (exception is AppGatewayAccessDeniedException or NotSupportedException or FormatException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>
    /// No installed app declares the file's extension for <c>open</c>: the box then says so above the
    /// list of every app (the File Explorer shows it this way when the open answered <c>NoHandler</c>
    /// with Store apps to propose, ADR 0257 §4).
    /// </summary>
    private bool NoInstalledHandler => _extension is not null
        && _storeApps is { Count: > 0 }
        && !_installed.Any(app => app.FileHandlers.Any(handler =>
            handler.Extensions.Any(extension => string.Equals(extension, _extension, StringComparison.OrdinalIgnoreCase))
            && handler.Actions.Any(action => string.Equals(action, "open", StringComparison.OrdinalIgnoreCase))));

    /// <summary>The app's short description in the language of the explorer's texts, else English.</summary>
    private string ShortDescription(AppIntentSuggestedCatalogApp app)
    {
        CultureInfo culture = L is KazoStringLocalizer kazo ? kazo.Culture : KazoLocale.DefaultLanguage;
        foreach (string key in new[] { culture.Name, culture.TwoLetterISOLanguageName, "en" })
        {
            if (key.Length > 0 && app.ShortDescriptions.TryGetValue(key, out string? text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// « View in the Store » (ADR 0257 §4, §5): the Store opens on the app's page, where the install
    /// keeps its usual consent (capabilities, scope, administrator), and reopens the file once the app
    /// is installed. The box closes without opening anything.
    /// </summary>
    private async Task ShowInStoreAsync(AppIntentSuggestedCatalogApp app)
    {
        _storeError = null;
        try
        {
            AppIntentLaunchResult launched = await HostContext.Intents.LaunchAsync(
                KazoApps.StoreAppId,
                [StoreAppArgument + app.AppId, StoreOpenAfterInstallArgument + _openWithPath],
                HostContext.CancellationToken);
            if (launched.Outcome != AppIntentLaunchOutcome.Launched)
            {
                _storeError = L["OpenWith.StoreFailed"];
                return;
            }
        }
        catch (Exception exception) when (exception is AppGatewayAccessDeniedException or NotSupportedException)
        {
            _storeError = L["OpenWith.StoreFailed"];
            return;
        }

        await CancelAsync();
    }

    /// <summary>The Store's argument naming the app page to open.</summary>
    internal const string StoreAppArgument = "--app=";

    /// <summary>The Store's argument naming the file to open after the install (ADR 0257 §5).</summary>
    internal const string StoreOpenAfterInstallArgument = "--open-after-install=";

    /// <summary>Reads the entry with the requesting app's rights: what it may not stat, it does not see.</summary>
    private async Task LoadMetadataAsync(string path)
    {
        try
        {
            FileSystemResult<FileSystemEntrySnapshot> stat = await HostContext.FileSystem.StatAsync(
                new FileSystemStatRequest(VirtualPath.Parse(path)), HostContext.CancellationToken);
            if (stat.Succeeded)
            {
                _metadata = stat.Value!.Metadata;
                if (_metadata.Kind == FileSystemEntryKind.Directory)
                {
                    _ = MeasureFolderAsync(VirtualPath.Parse(path));
                }
            }
            else
            {
                _statError = L["Error.Item", NameOf(path), stat.Error?.Code.ToString() ?? string.Empty];
            }
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or AppGatewayAccessDeniedException)
        {
            _statError = L["Error.Item", NameOf(path), exception.Message];
        }
    }

    /// <summary>A folder's total size (ADR 0185 §5), measured after the dialog shows, with the asking app's rights.</summary>
    private async Task MeasureFolderAsync(VirtualPath path)
    {
        try
        {
            FileSystemResult<FileSystemTreeSize> measured = await HostContext.FileSystem.MeasureAsync(path, HostContext.CancellationToken);
            _folderSize = measured.Succeeded ? measured.Value : null;
            _folderSizeFailed = !measured.Succeeded;
        }
        catch (Exception exception) when (exception is AppGatewayAccessDeniedException or OperationCanceledException or InvalidOperationException)
        {
            _folderSizeFailed = true;
        }

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>The Size line: a file's length, a folder's total with its content counts, or "--".</summary>
    private string SizeText => _metadata switch
    {
        FileMetadata file => L["Properties.Bytes", file.Length.ToString("N0", FormatCulture)],
        { Kind: FileSystemEntryKind.Directory } when _folderSize is { } size => L[
            size.IsComplete ? "Properties.FolderSize" : "Properties.FolderSizePartial",
            size.Length.ToString("N0", FormatCulture),
            size.FileCount.ToString("N0", FormatCulture),
            size.DirectoryCount.ToString("N0", FormatCulture)],
        { Kind: FileSystemEntryKind.Directory } when !_folderSizeFailed => L["Properties.Measuring"],
        _ => "--"
    };

    private Task ChooseApp(string? appId) =>
        appId is null ? Task.CompletedTask : CompleteAsync(new OpenWithDialogResult(OpenWithDialogStatus.Selected, appId, CanRemember && _alwaysUse));

    private Task ClosePropertiesAsync() => CompleteAsync(new FilePropertiesDialogResult(FilePropertiesDialogStatus.Closed));

    private Task ResolveAsync(FileConflictDialogChoice choice) => CompleteAsync(new FileConflictDialogResult(choice, _applyToAll));

    private Task CancelAsync() => CompleteAsync(null);

    /// <summary>Answers the OS once; later clicks (a double click's second event) are ignored.</summary>
    private async Task CompleteAsync(object? result)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        await Completed.InvokeAsync(result);
    }

    /// <summary>Arrow keys move the choice, Enter opens it, Escape cancels, like a list box.</summary>
    private async Task OnAppListKeyDown(KeyboardEventArgs e)
    {
        IReadOnlyList<KazoAppInfo> apps = VisibleApps;
        if (apps.Count == 0)
        {
            return;
        }

        int index = apps.ToList().FindIndex(app => app.Id == _selectedAppId);
        switch (e.Key)
        {
            case "ArrowDown":
                _selectedAppId = apps[Math.Min(apps.Count - 1, index + 1)].Id;
                break;
            case "ArrowUp":
                _selectedAppId = apps[Math.Max(0, index - 1)].Id;
                break;
            case "Enter":
                await ChooseApp(_selectedAppId);
                break;
            case "Escape":
                await CancelAsync();
                break;
        }
    }

    // A conflict on a folder and on a file are worded apart; the name is set in bold by the markup.
    private (string Before, string After) ExistsText(FileConflictDialogRequest conflict) =>
        Strings.Around(L[conflict.DestinationIsDirectory ? "Conflict.FolderExists" : "Conflict.FileExists"]);

    // The entry kind in words; kinds without a text keep their enum name.
    private string KindText(FileSystemEntryKind kind) => kind switch
    {
        FileSystemEntryKind.File => L["Kind.File"],
        FileSystemEntryKind.Directory => L["Kind.Directory"],
        FileSystemEntryKind.SymbolicLink => L["Kind.SymbolicLink"],
        _ => kind.ToString()
    };

    private static string NameOf(string path)
    {
        string trimmed = path.TrimEnd('/');
        int separator = trimmed.LastIndexOf('/');
        return trimmed.Length == 0 ? "/" : separator < 0 ? trimmed : trimmed[(separator + 1)..];
    }
}
