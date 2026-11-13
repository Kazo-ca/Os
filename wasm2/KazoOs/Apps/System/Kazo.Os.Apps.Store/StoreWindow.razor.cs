using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Globalization;
using Kazo.Os.Simulation.Abstractions.Gateways;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Apps.Store;

/// <summary>The page the Store shows.</summary>
internal enum StoreView
{
    /// <summary>Categories, or the search results.</summary>
    Home,

    /// <summary>The « Installed » tab, with the updates.</summary>
    Installed,

    /// <summary>The « Themes » tab: the theme packs of the catalogs (ADR 0213 §5).</summary>
    Themes,

    /// <summary>One app's page.</summary>
    Detail
}

/// <summary>
/// The Kazo OS Store window (ADR 0188 §1 to §3, ADR 0212): home by categories, search, an app's page,
/// Install for me / for everyone, Open, Update, Uninstall, the « Installed » tab and « Restart to
/// finish ». The same component is the desktop window and the mobile screen.
/// </summary>
/// <remarks>
/// <para>
/// Only the public SDK: <see cref="KazoAppPackages"/> reads the catalogs (downloaded by the OS
/// itself), installs and removes; the OS asks an administrator in its own windows when the install is
/// for every user, from an untrusted source or of a blocked app (ADR 0189 §4). The Store never sees
/// a password.
/// </para>
/// <para>
/// Launch arguments: <c>--page=installed</c> opens the « Installed » tab (the update notification
/// does), <c>--page=themes</c> the « Themes » tab, <c>--app=&lt;appId&gt;</c> opens an app's page,
/// and <c>--open-after-install=&lt;path&gt;</c> opens that file once the app is installed (ADR 0257 §5,
/// the File Explorer's « View in the Store »). A Store already open receives them too.
/// </para>
/// <para>
/// The « Themes » tab (ADR 0213 §5) lists the theme packs of the catalogs: install for me or for
/// everyone, update, remove, and a button that opens Settings ▸ Appearance, where the pack's parts
/// are chosen. A pack applies at once: it never waits for a restart.
/// </para>
/// </remarks>
public partial class StoreWindow
{
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private StoreCatalog _store = StoreCatalog.Empty;
    private StoreThemePacks _themePacks = StoreThemePacks.Empty;
    private bool _themePacksUnsupported;
    private StoreView _view = StoreView.Home;
    private StoreView _returnView = StoreView.Home;
    private string? _query;
    private string? _selectedAppId;
    private string? _categoryId;
    private bool _isLoading = true;
    private StoreOperations? _operations;
    private bool _isRestarting;
    private string? _message;
    private bool _messageIsError;
    private string? _description;
    private (string AppId, string Path)? _openAfterInstall;
    private string? _descriptionKey;
    private bool _isDescriptionLoading;
    private IStringLocalizer<Strings>? _localizer;

    /// <summary>The window's texts in the language of the Store process (ADR 0104).</summary>
    private IStringLocalizer<Strings> L => _localizer ??= AppContext.GetLocalizer<Strings>();

    /// <summary>The language of the Store process, for the catalogs' texts.</summary>
    private CultureInfo Culture => L is KazoStringLocalizer kazo ? kazo.Culture : KazoLocale.DefaultLanguage;

    private StoreEntry? Selected => _selectedAppId is null ? null : _store.Find(_selectedAppId);

    /// <summary>
    /// The actions running or waiting on each app (ADR 0266 §7): only their app's buttons are disabled,
    /// and each redraws the window as the OS reports its steps.
    /// </summary>
    private StoreOperations Operations => _operations ??= new StoreOperations(() => _ = InvokeAsync(StateHasChanged));

    /// <summary>Whether the Store restarts the OS or works on any app: the global buttons wait.</summary>
    private bool IsBusy => _isRestarting || Operations.Any;

    private IReadOnlyList<StoreEntry> SearchResults => _store.Search(_query, Culture);

    private IReadOnlyList<StoreEntry> InstalledResults => _store.SearchInstalled(_query, Culture);

    private bool IsSearching => !string.IsNullOrWhiteSpace(_query);

    /// <summary>The page on screen, for <see cref="StoreScrollMemory"/>.</summary>
    private string ScrollViewKey => StoreScrollMemory.ViewKey(_view, _selectedAppId, _categoryId, IsSearching);

    /// <summary>
    /// Draws <see cref="StoreScrollMemory"/> as the first child of the page. Written by hand: an
    /// internal component written in C# is not found by the Razor markup.
    /// </summary>
    private RenderFragment RenderScrollMemory() => builder =>
    {
        builder.OpenComponent<StoreScrollMemory>(0);
        builder.AddComponentParameter(1, nameof(StoreScrollMemory.View), ScrollViewKey);
        builder.CloseComponent();
    };

    /// <inheritdoc />
    protected override async Task OnAppInitializedAsync()
    {
        ApplyArguments(StoreLaunchArguments.Parse(LaunchArguments));
        await LoadAsync(refresh: false);
        await ShowRequestedDetailAsync();
    }

    /// <summary>
    /// The Store is a single window per user: another app's launch (the File Explorer's « View in the
    /// Store ») reaches the open window here with its arguments.
    /// </summary>
    protected override async Task OnIntentReceivedAsync(AppIntentDelivery intent)
    {
        StoreLaunchArguments arguments = StoreLaunchArguments.Parse(intent.Arguments);
        if (arguments.View is null)
        {
            return;
        }

        ApplyArguments(arguments);
        await ShowRequestedDetailAsync();
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>Takes the page, the app and the file to open after its install from the command line.</summary>
    private void ApplyArguments(StoreLaunchArguments arguments)
    {
        if (arguments.View is { } view)
        {
            _view = view;
            _selectedAppId = arguments.AppId;
            _openAfterInstall = arguments.OpenAfterInstall is { } path && arguments.AppId is { } appId
                ? (appId, path)
                : null;
        }
    }

    /// <summary>Loads the requested app's description, or goes home when no catalog lists it.</summary>
    private async Task ShowRequestedDetailAsync()
    {
        if (_view == StoreView.Detail && Selected is { } entry)
        {
            await LoadDescriptionAsync(entry);
        }
        else if (_view == StoreView.Detail)
        {
            _view = StoreView.Home;
            _selectedAppId = null;
            _openAfterInstall = null;
        }
    }

    /// <summary>Reads the catalogs, what is installed and the updates.</summary>
    private async Task LoadAsync(bool refresh)
    {
        _isLoading = true;
        try
        {
            IReadOnlyList<KazoAppCatalog> catalogs = await KazoAppPackages.GetCatalogsAsync(refresh);
            IReadOnlyList<KazoInstalledPackage> installed = await KazoAppPackages.GetInstalledAsync();
            IReadOnlyList<KazoPackageUpdate> updates = await KazoAppPackages.GetUpdatesAsync();
            _store = new StoreCatalog(catalogs, installed, updates, _removed);
            _themePacks = new StoreThemePacks(catalogs, await ReadInstalledThemePacksAsync());
        }
        catch (NotSupportedException)
        {
            ShowError(L["Error.Unsupported"]);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or AppGatewayAccessDeniedException)
        {
            ShowError(L["Error.NotAllowed"]);
        }
        catch (Exception exception) when (exception is KazoPackageException or IOException)
        {
            ShowError(L["Error.Generic", exception.Message]);
        }
        finally
        {
            _isLoading = false;
        }
    }

    /// <summary>Reads the installed packs; an OS without theme packs keeps the apps working.</summary>
    private async Task<IReadOnlyList<KazoInstalledThemePack>> ReadInstalledThemePacksAsync()
    {
        try
        {
            IReadOnlyList<KazoInstalledThemePack> packs = await KazoAppPackages.GetInstalledThemePacksAsync();
            _themePacksUnsupported = false;
            return packs;
        }
        catch (NotSupportedException)
        {
            _themePacksUnsupported = true;
            return [];
        }
    }

    private Task RefreshAsync()
    {
        _message = null;
        return LoadAsync(refresh: true);
    }

    private void ShowHome()
    {
        _view = StoreView.Home;
        _categoryId = null;
        _selectedAppId = null;
    }

    private void ShowInstalled()
    {
        _view = StoreView.Installed;
        _selectedAppId = null;
    }

    private void ShowThemes()
    {
        _view = StoreView.Themes;
        _selectedAppId = null;
        _categoryId = null;
    }

    private void ShowCategory(string categoryId)
    {
        _view = StoreView.Home;
        _query = null;
        _categoryId = categoryId;
    }

    private void OnQueryChanged(Microsoft.AspNetCore.Components.ChangeEventArgs args)
    {
        _query = args.Value?.ToString();
        // The search box filters the packs while the « Themes » tab is open, and the installed
        // apps while « Installed » is.
        if (_view is StoreView.Themes or StoreView.Installed)
        {
            return;
        }

        if (_view == StoreView.Detail || IsSearching)
        {
            _view = StoreView.Home;
            _categoryId = null;
        }
    }

    private async Task OpenDetailAsync(StoreEntry entry)
    {
        _returnView = _view == StoreView.Detail ? _returnView : _view;
        _selectedAppId = entry.AppId;
        _view = StoreView.Detail;
        await LoadDescriptionAsync(entry);
    }

    private void CloseDetail()
    {
        _view = _returnView;
        _selectedAppId = null;
    }

    private async Task LoadDescriptionAsync(StoreEntry entry)
    {
        if (entry.App is not { } app)
        {
            _description = null;
            return;
        }

        string key = $"{app.Source}@{app.LatestVersion}:{Culture.TwoLetterISOLanguageName}";
        if (key == _descriptionKey)
        {
            return;
        }

        _descriptionKey = key;
        _description = null;
        _isDescriptionLoading = true;
        try
        {
            _description = await KazoAppPackages.GetCatalogDescriptionAsync(app, language: Culture.TwoLetterISOLanguageName);
        }
        catch (Exception exception) when (exception is KazoPackageException or IOException or NotSupportedException)
        {
            // The page still shows the short description and the facts.
            _description = null;
        }
        finally
        {
            _isDescriptionLoading = false;
        }
    }

    private Task InstallAsync(StoreEntry entry, KazoPackageScope scope) =>
        RunAsync(entry, async () =>
        {
            KazoCatalogApp app = entry.App!;
            KazoPackageInstallReport report = await KazoAppPackages.InstallAsync(
                app.Source, scope, Operations.ProgressFor(entry.AppId, app.Latest.SizeBytes));
            _removed.Remove(entry.AppId);
            ShowInfo(L[report.SystemWide ? "Message.InstalledForEveryone" : "Message.InstalledForMe", report.Name, report.Version]);
            await OpenFileAfterInstallAsync(report);
        }, firstStage: StoreOperationStage.Preparing);

    /// <summary>
    /// ADR 0257 §5: the Store was opened for a file no app opened. Once that app is installed, the
    /// file is opened again through the OS's resolution, which now finds it; when another app could
    /// open it too, the new app opens it, since it is the one the user just chose. An app that waits
    /// for a restart cannot open it yet: the message says so, and nothing else happens.
    /// </summary>
    private async Task OpenFileAfterInstallAsync(KazoPackageInstallReport report)
    {
        if (_openAfterInstall is not ({ } appId, { } path) || !string.Equals(appId, report.AppId, StringComparison.Ordinal))
        {
            return;
        }

        _openAfterInstall = null;
        if (report.RestartRequired)
        {
            ShowInfo(L["Message.OpenAfterRestart", report.Name, path]);
            return;
        }

        try
        {
            KazoOpenFileResult opened = await KazoApps.OpenFileAsync(path);
            if (opened.Status != KazoOpenFileStatus.Opened)
            {
                await KazoApps.LaunchAsync(report.AppId, [path]);
            }
        }
        catch (Exception exception) when (exception is KazoAppLaunchException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            ShowError(L["Error.OpenAfterInstall", path, exception.Message]);
        }
    }

    private Task UpdateAsync(StoreEntry entry) =>
        RunAsync(entry, async () =>
        {
            KazoPackageUpdate update = entry.Update!;
            long? catalogBytes = entry.App?.Versions.FirstOrDefault(version => version.Version == update.AvailableVersion)?.SizeBytes;
            KazoPackageInstallReport report = await KazoAppPackages.InstallAsync(
                $"{update.Source}@{update.AvailableVersion}",
                update.SystemWide ? KazoPackageScope.System : KazoPackageScope.User,
                Operations.ProgressFor(entry.AppId, catalogBytes));
            ShowInfo(L["Message.Updated", report.Name, report.Version]);
        }, firstStage: StoreOperationStage.Preparing);

    private async Task UpdateAllAsync()
    {
        foreach (StoreEntry entry in _store.Updatable)
        {
            await UpdateAsync(entry);
            if (_messageIsError)
            {
                return;
            }
        }
    }

    private Task RemoveAsync(StoreEntry entry) =>
        RunAsync(entry, async () =>
        {
            KazoInstalledPackage removed = await KazoAppPackages.RemoveAsync(
                entry.AppId,
                entry.Installed?.SystemWide == true ? KazoPackageScope.System : KazoPackageScope.User);
            // Hot-add (ADR 0214 §1): only an app loaded in the page waits for the restart.
            if (removed.Loaded)
            {
                _removed.Add(removed.AppId);
            }

            ShowInfo(L[removed.Loaded ? "Message.RemovedRestart" : "Message.Removed", entry.Name]);
        });

    private Task RollbackAsync(StoreEntry entry) =>
        RunAsync(entry, async () =>
        {
            KazoPackageInstallReport report = await KazoAppPackages.RollbackAsync(
                entry.AppId,
                entry.Installed?.SystemWide == true ? KazoPackageScope.System : KazoPackageScope.User);
            ShowInfo(L[report.RestartRequired ? "Message.RolledBackRestart" : "Message.RolledBack", report.Name, report.Version]);
        });

    private Task OpenAsync(StoreEntry entry) =>
        RunAsync(entry, async () => await KazoApps.LaunchAsync(entry.AppId), reload: false, exclusive: false);

    private Task InstallThemePackAsync(StoreThemePackEntry entry, KazoPackageScope scope) =>
        RunAsync(entry.PackId, async () =>
        {
            KazoThemePackInstallReport report = await KazoAppPackages.InstallThemePackAsync(entry.Pack!.Source, scope);
            ShowInfo(L[report.SystemWide ? "Message.PackInstalledForEveryone" : "Message.PackInstalledForMe", report.Name, report.Version]);
        });

    private Task UpdateThemePackAsync(StoreThemePackEntry entry) =>
        RunAsync(entry.PackId, async () =>
        {
            KazoThemePackInstallReport report = await KazoAppPackages.InstallThemePackAsync(
                $"{entry.Pack!.Source}@{entry.AvailableUpdate}",
                entry.Installed!.SystemWide ? KazoPackageScope.System : KazoPackageScope.User);
            ShowInfo(L["Message.PackUpdated", report.Name, report.Version]);
        });

    private Task RemoveThemePackAsync(StoreThemePackEntry entry) =>
        RunAsync(entry.PackId, async () =>
        {
            KazoInstalledThemePack removed = await KazoAppPackages.RemoveThemePackAsync(
                entry.PackId,
                entry.Installed!.SystemWide ? KazoPackageScope.System : KazoPackageScope.User);
            ShowInfo(L["Message.PackRemoved", removed.Name]);
        });

    /// <summary>Opens Settings ▸ Appearance, where the pack's theme, wallpaper, icons and cursors are chosen (Q3 A).</summary>
    private Task OpenAppearanceAsync(StoreThemePackEntry entry) =>
        RunAsync(entry.PackId, async () => await KazoApps.LaunchAsync(SettingsAppId, [AppearancePageArgument]), reload: false, exclusive: false);

    private const string SettingsAppId = "org.kazoos.settings";
    private const string AppearancePageArgument = "--page=appearance";

    private IReadOnlyList<StoreThemePackEntry> VisibleThemePacks => _themePacks.Search(_query, Culture);

    private string PartsText(KazoThemePackParts parts) => string.Join(" · ", Enum.GetValues<KazoThemePackParts>()
        .Where(part => part != KazoThemePackParts.None && parts.HasFlag(part))
        .Select(part => L["Part." + part].Value));

    private async Task RestartAsync()
    {
        _isRestarting = true;
        try
        {
            await KazoAppPackages.RestartToFinishAsync();
        }
        catch (KazoPackageException exception) when (exception.Code == "apps.package.nothing-pending")
        {
            ShowInfo(L["Message.NothingPending"]);
        }
        catch (Exception exception) when (exception is KazoPackageException or NotSupportedException or UnauthorizedAccessException or AppGatewayAccessDeniedException)
        {
            ShowError(L["Error.Generic", exception.Message]);
        }
        finally
        {
            _isRestarting = false;
        }
    }

    /// <summary>Runs one action on an app, then reads the Store again so every button follows.</summary>
    private Task RunAsync(
        StoreEntry entry, Func<Task> action, bool reload = true, StoreOperationStage firstStage = StoreOperationStage.Working, bool exclusive = true) =>
        RunAsync(entry.AppId, action, reload, firstStage, exclusive);

    /// <summary>
    /// Runs one action on an app or a pack, then reads the Store again so every button follows. An
    /// action that changes what is installed (<paramref name="exclusive"/>) waits for the one running
    /// (ADR 0266 §7); a launch does not.
    /// </summary>
    private async Task RunAsync(
        string id, Func<Task> action, bool reload = true, StoreOperationStage firstStage = StoreOperationStage.Working, bool exclusive = true)
    {
        _message = null;
        await Operations.RunAsync(id, firstStage, exclusive, () => RunReportingAsync(action, reload));
    }

    /// <summary>Runs the action and turns its failure into the Store's message.</summary>
    private async Task RunReportingAsync(Func<Task> action, bool reload)
    {
        try
        {
            await action();
            if (reload)
            {
                await LoadAsync(refresh: false);
            }
        }
        catch (KazoPackageException exception)
        {
            ShowError(DescribePackageError(exception));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or AppGatewayAccessDeniedException)
        {
            ShowError(L["Error.NotApproved"]);
        }
        catch (Exception exception) when (exception is KazoAppLaunchException or NotSupportedException or IOException)
        {
            ShowError(L["Error.Generic", exception.Message]);
        }
    }

    /// <summary>
    /// The step of an action in words (ADR 0266 §7), such as « Téléchargement… 45 % (1,2 Mo sur
    /// 2,7 Mo) », instead of a bare « Working… ».
    /// </summary>
    /// <param name="operation">The action.</param>
    /// <param name="compact">On a card: the percent alone, without the sizes.</param>
    private string OperationText(StoreOperation operation, bool compact = false) => operation.Stage switch
    {
        StoreOperationStage.Queued => L["Progress.Queued"],
        StoreOperationStage.Downloading when compact && operation.Percent is { } shortPercent => L["Progress.DownloadingShort", shortPercent],
        StoreOperationStage.Preparing => L["Progress.Preparing"],
        StoreOperationStage.Downloading when operation.Percent is { } percent =>
            L["Progress.DownloadingPercent", percent, SizeText(operation.BytesReceived ?? 0), SizeText(operation.TotalBytes ?? 0)],
        StoreOperationStage.Downloading when operation.BytesReceived is > 0 and { } received => L["Progress.DownloadingBytes", SizeText(received)],
        StoreOperationStage.Downloading => L["Progress.Downloading"],
        StoreOperationStage.Verifying => L["Progress.Verifying"],
        StoreOperationStage.AwaitingApproval => L["Progress.AwaitingApproval"],
        StoreOperationStage.Installing => L["Progress.Installing"],
        _ => L["Working"]
    };

    private string DescribePackageError(KazoPackageException exception) => exception.Code switch
    {
        "apps.package.blocked" => L["Error.Blocked"],
        "apps.package.installed-for-system" => L["Error.InstalledForSystem"],
        "apps.package.sdk-incompatible" => L["Error.Incompatible", exception.Message],
        "apps.package.missing-api" => L["Error.Incompatible", exception.Message],
        "apps.package.download-failed" => L["Error.Offline"],
        "apps.package.no-previous" => L["Error.NoPrevious"],
        "apps.package.reinstall-required" => L["Error.ReinstallRequired"],
        "apps.package.unsigned" => L["Error.Unsigned"],
        "apps.package.bad-signature" => L["Error.BadSignature"],
        { } code when code.StartsWith("themes.pack.", StringComparison.Ordinal) => L["Error.PackRefused", exception.Message],
        _ => L["Error.Generic", exception.Message]
    };

    private void ShowInfo(string message)
    {
        _message = message;
        _messageIsError = false;
    }

    private void ShowError(string message)
    {
        _message = message;
        _messageIsError = true;
    }

    private string CategoryLabel(string categoryId) =>
        categoryId == StoreCatalog.OtherCategoryId
            ? L["Category.Other"]
            : _store.Categories.FirstOrDefault(category => category.Id == categoryId)?.GetLabel(Culture) ?? categoryId;

    private string TrustText(KazoPackageTrust trust) => trust switch
    {
        KazoPackageTrust.Verified => L["Trust.Verified"],
        KazoPackageTrust.Trusted => L["Trust.Trusted"],
        _ => L["Trust.Untrusted"]
    };

    private static string TrustClass(KazoPackageTrust trust) => trust switch
    {
        KazoPackageTrust.Verified => "is-verified",
        KazoPackageTrust.Trusted => "is-trusted",
        _ => "is-untrusted"
    };

    private static string TrustIcon(KazoPackageTrust trust) => trust switch
    {
        KazoPackageTrust.Verified => "shield-check",
        KazoPackageTrust.Trusted => "shield",
        _ => "shield-alert"
    };

    private string StateText(StoreEntry entry) => entry.State switch
    {
        StoreAppState.Installed => entry.Installed!.SystemWide ? L["State.InstalledForEveryone"] : L["State.InstalledForMe"],
        StoreAppState.UpdateAvailable => L["State.UpdateAvailable", entry.Update!.AvailableVersion],
        StoreAppState.PendingRestart => L["State.PendingRestart"],
        _ => string.Empty
    };

    private string SizeText(long bytes) => StoreCatalog.FormatSize(bytes, Culture);
}
