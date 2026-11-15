using System.Text.Json;
using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Apps;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Craft;
using Kazo.Os.AppSdk.IO;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kazo.Os.Fixtures.CraftApp;

/// <summary>
/// A Craft app window built on the <c>Kazo.Os.AppSdk.Craft</c> module (ADR 0259), the way
/// <c>doc/en/wasm/framework/craft-apps.md</c> describes: a <see cref="CraftFrame"/> shows the app's
/// iframe page (<c>wwwroot/host/index.html</c>), a loading screen covers it until <c>ready</c>, an
/// error screen with "Retry" replaces it on <c>failed</c> or after <see cref="FrameStartup.Timeout"/>.
/// </summary>
/// <remarks>
/// This file holds the frame's life and what the window passes on (theme, language, settings,
/// reserved chords, file picker); <c>CraftFixtureWindow.Documents.cs</c> holds opening, saving and
/// the close guard. The Craft app behind it is a fake (<c>wwwroot/fakecraft/</c>) the end-to-end
/// journey drives. Aucun jeu sur ce repository.
/// </remarks>
public sealed partial class CraftFixtureWindow : IAsyncDisposable
{
    /// <summary>The languages the fake Craft app has; any other culture gets English.</summary>
    private static readonly string[] Languages = [CraftLanguage.English, "fr"];

    /// <summary>
    /// The window's reserved chords: the OS's and Ctrl+N (<see cref="ReservedKeys.All"/>), plus Save As
    /// (F12, Ctrl+Shift+S), which the window decides itself, as for an app whose save never says
    /// <c>saveAs</c> (GridCraft).
    /// </summary>
    private static readonly IReadOnlyList<ReservedKey> Chords =
    [
        .. ReservedKeys.All,
        ReservedKeys.Parse("F12", ReservedKeyTarget.App),
        ReservedKeys.Parse("Ctrl+Shift+S", ReservedKeyTarget.App),
    ];

    private readonly FrameStartup _startup = new();
    private ElementReference _frameHost;
    private CraftFrame? _frame;
    private CancellationTokenSource? _timeout;
    private WebStorageFile? _settings;
    private OpenRequest _request = new(null, false);

    [Inject]
    private IJSRuntime JavaScript { get; set; } = default!;

    private string FailureText => _startup.TimedOut
        ? $"No answer after {(int)FrameStartup.Timeout.TotalSeconds} s."
        : _startup.Error ?? string.Empty;

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        CancelTimeout();
        await StopFrameAsync();
        _nextSave?.TrySetResult(false);
        _saveGate.Dispose();
    }

    /// <inheritdoc />
    protected override async Task OnAppInitializedAsync()
    {
        _request = OpenRequest.FromArguments(LaunchArguments);
        await SetWindowTitleAsync(DocumentTitle);
    }

    /// <inheritdoc />
    protected override async Task OnAppAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await StartFrameAsync();
        }
    }

    /// <inheritdoc />
    protected override async Task OnWindowActivatedAsync()
    {
        if (_frame is not null && _startup.State == FrameState.Running)
        {
            await _frame.FocusAsync();
        }
    }

    private async Task StartFrameAsync()
    {
        CraftFrame frame = new(JavaScript);
        Connect(frame, connect: true);
        _frame = frame;
        ArmTimeout(_startup.Attempt);

        CraftFrameOptions options = new(CraftFrameOptions.PagePathOf(typeof(CraftFixtureWindow).Assembly.GetName().Name!), "Craft Fixture")
        {
            Language = CraftLanguage.Pick(AppContext.Culture, Languages),
            ReservedKeys = Chords,
            Settings = await LoadSettingsAsync(),
        };
        try
        {
            await frame.StartAsync(_frameHost, options, AppContext.CancellationToken);
        }
        catch (Exception exception) when (exception is JSException or InvalidOperationException)
        {
            await OnFailedAsync(exception.Message);
        }
    }

    // Every event of one frame; "Retry" drops the old frame's before making a new one.
    private void Connect(CraftFrame frame, bool connect)
    {
        if (connect)
        {
            frame.Ready += OnReadyAsync;
            frame.Failed += OnFailedAsync;
            frame.DirtyChanged += OnDirtyChangedAsync;
            frame.Saving += OnSavingAsync;
            frame.ErrorReported += OnErrorReportedAsync;
            frame.KeyPressed += OnKeyPressedAsync;
            frame.PickRequested += OnPickRequestedAsync;
            frame.SettingChanged += OnSettingChangedAsync;
            frame.SchemeChanged += OnSchemeChangedAsync;
        }
        else
        {
            frame.Ready -= OnReadyAsync;
            frame.Failed -= OnFailedAsync;
            frame.DirtyChanged -= OnDirtyChangedAsync;
            frame.Saving -= OnSavingAsync;
            frame.ErrorReported -= OnErrorReportedAsync;
            frame.KeyPressed -= OnKeyPressedAsync;
            frame.PickRequested -= OnPickRequestedAsync;
            frame.SettingChanged -= OnSettingChangedAsync;
            frame.SchemeChanged -= OnSchemeChangedAsync;
        }
    }

    private async Task StopFrameAsync()
    {
        if (_frame is null)
        {
            return;
        }

        Connect(_frame, connect: false);
        await _frame.DisposeAsync();
        _frame = null;
    }

    private async Task RetryAsync()
    {
        CancelTimeout();
        await StopFrameAsync();
        _startup.Retry();
        StateHasChanged();
        await StartFrameAsync();
    }

    private async Task OnReadyAsync(string? appVersion)
    {
        if (!_startup.OnReady(appVersion))
        {
            return;
        }

        CancelTimeout();
        await InvokeAsync(StateHasChanged);
        if (_frame is not { } frame)
        {
            return;
        }

        // The Craft app takes the OS theme and the user's language before the document.
        await OnSchemeChangedAsync(await frame.GetColorSchemeAsync());
        await frame.RequestAsync("app.language.set", new { language = CraftLanguage.Pick(AppContext.Culture, Languages) }, cancellationToken: AppContext.CancellationToken);
        await PrepareDocumentAsync();
        if (IsWindowActive)
        {
            await frame.FocusAsync();
        }
    }

    private async Task OnFailedAsync(string? error)
    {
        if (_startup.OnFailed(error))
        {
            CancelTimeout();
            await InvokeAsync(StateHasChanged);
        }
    }

    // The OS theme changed (or the app just started): the Craft app follows its color scheme.
    private async Task OnSchemeChangedAsync(string scheme)
    {
        if (_frame is { } frame)
        {
            await frame.RequestAsync("ui.set", new { dark = scheme != "light" }, cancellationToken: AppContext.CancellationToken);
        }
    }

    // A failure after ready is about a document or a save: the app keeps running, the user is told.
    private Task OnErrorReportedAsync(FrameError error) =>
        ShowNoticeAsync($"The app reported an error: {error.Message}", error: true);

    // F12 and Ctrl+Shift+S: Save As, decided by the window; Ctrl+N: another window of the app.
    private async Task OnKeyPressedAsync(ReservedKey key)
    {
        using IDisposable scope = KazoExecutionContext.Enter(AppContext);
        if (key with { Target = ReservedKeyTarget.App } == ReservedKeys.NewWindow)
        {
            try
            {
                await KazoApps.LaunchAsync(CraftFixtureApp.AppId, [], AppContext.CancellationToken);
            }
            catch (Exception exception) when (exception is KazoAppLaunchException or UnauthorizedAccessException)
            {
                await ShowNoticeAsync($"No new window: {exception.Message}", error: true);
            }

            return;
        }

        await InvokeAsync(SaveAsAsync);
    }

    // The Craft app opened its file picker: the OS Open dialog answers it, with the file's bytes.
    private Task OnPickRequestedAsync(PickRequest request) => InvokeAsync(async () =>
    {
        using IDisposable scope = KazoExecutionContext.Enter(AppContext);
        CraftFrame? frame = _frame;
        if (frame is null)
        {
            return;
        }

        List<PickedFile>? files = null;
        try
        {
            OpenFileDialogResult choice = await OpenFileAsync(
                new OpenFileDialogRequest
                {
                    Title = "Pick a file for the Craft app",
                    InitialDirectory = VirtualPath.Parse(AppContext.HomeDirectory),
                    AllowMultiple = request.Multiple,
                    Filters = request.Extensions.Count > 0
                        ? [new FileDialogFilter("Accepted files", request.Extensions, []), new FileDialogFilter("All files", [], [])]
                        : [new FileDialogFilter("All files", [], [])],
                },
                AppContext.CancellationToken);
            if (choice.Status == FileDialogStatus.Selected)
            {
                files = [];
                foreach (SelectedFileResource resource in choice.Resources)
                {
                    string path = resource.Path.Value;
                    byte[] bytes = await KazoFile.ReadAllBytesAsync(path, AppContext.CancellationToken);
                    files.Add(new PickedFile(CraftDocumentFormats.FileNameOf(path), string.Empty, bytes));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            files = null;
            await ShowNoticeAsync($"The file could not be read: {exception.Message}", error: true);
        }
        finally
        {
            // The Craft app waits on its picker until it hears back: always answer, even on a failure.
            await frame.AnswerPickAsync(request.Id, files);
        }
    });

    // The user's settings for the Craft app, read before the iframe starts (the app reads them as it starts).
    private async Task<IReadOnlyDictionary<string, string>?> LoadSettingsAsync()
    {
        _settings ??= new WebStorageFile(AppContext.HomeDirectory, CraftFixtureApp.AppId);
        using IDisposable scope = KazoExecutionContext.Enter(AppContext);
        try
        {
            return new Dictionary<string, string>(await _settings.LoadAsync(AppContext.CancellationToken), StringComparer.Ordinal);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // The Craft app wrote a setting: the user's file follows at once (a real app may gather writes).
    private async Task OnSettingChangedAsync(string key, string? value)
    {
        if (_settings is null || !_settings.Set(key, value))
        {
            return;
        }

        using IDisposable scope = KazoExecutionContext.Enter(AppContext);
        try
        {
            await _settings.SaveAsync(AppContext.CancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            // The setting stays in this window's Craft app; the next write tries again.
        }
    }

    private void ArmTimeout(int attempt)
    {
        CancelTimeout();
        CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(AppContext.CancellationToken);
        _timeout = timeout;
        _ = WaitForTimeoutAsync(attempt, timeout.Token);
    }

    private async Task WaitForTimeoutAsync(int attempt, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(FrameStartup.Timeout, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await InvokeAsync(() =>
        {
            if (_startup.OnTimeout(attempt))
            {
                StateHasChanged();
            }
        });
    }

    private void CancelTimeout()
    {
        _timeout?.Cancel();
        _timeout?.Dispose();
        _timeout = null;
    }

    // The name the Craft app gives a new document (ui.inspect `title`), for the window title.
    private static string? TitleOf(JsonElement inspect) =>
        inspect.ValueKind == JsonValueKind.Object
        && inspect.TryGetProperty("title", out JsonElement title)
        && title.ValueKind == JsonValueKind.String
            ? title.GetString()
            : null;
}
