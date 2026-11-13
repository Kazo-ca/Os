using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Blazor;
using Kazo.Os.AppSdk.Craft;
using Kazo.Os.AppSdk.IO;
using Microsoft.JSInterop;

namespace Kazo.Os.Fixtures.CraftApp;

/// <summary>
/// Opening, saving and closing: the window reads and writes the files with <see cref="KazoFile"/>,
/// asks the OS dialogs, keeps the title and guards the close. Every decision comes from the module's
/// <see cref="DocumentSession"/>.
/// </summary>
public sealed partial class CraftFixtureWindow : IWindowCloseGuard
{
    private readonly DocumentSession _session = new(CraftFixtureApp.Formats, CraftFixtureApp.AppName);

    // Saves run one at a time: each may show a dialog, and the order is the Craft app's.
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    // Completed with the outcome of the next save (true = written), for the close guard.
    private TaskCompletionSource<bool>? _nextSave;

    // The next save is a Save As (F12, Ctrl+Shift+S): the fake app's save never says saveAs.
    private bool _saveAsRequested;

    // The name the Craft app gives a new document, read from ui.inspect once it is ready.
    private string _untitled = "Untitled";

    private string? _notice;
    private bool _noticeIsError;

    /// <summary>Gets the title the OS shows for this window: <c>*name — Craft Fixture</c>.</summary>
    private string DocumentTitle => _session.Title(_untitled, _startup.State == FrameState.Running);

    /// <summary>
    /// Asks before closing a window with work that is not in a file: the Craft app lists its
    /// documents (<c>ui.inspect</c>); "Yes" saves each one and closes once every write succeeded.
    /// </summary>
    /// <param name="cancellationToken">Cancels the close request.</param>
    /// <returns><see langword="true"/> to close.</returns>
    public async ValueTask<bool> ConfirmCloseAsync(CancellationToken cancellationToken = default)
    {
        if (_frame is not { } frame || _startup.State != FrameState.Running)
        {
            return true;
        }

        FrameCommandResult inspect = await frame.RequestAsync("ui.inspect", cancellationToken: cancellationToken);
        IReadOnlyList<OpenDocument> unsaved = inspect.Ok
            ? _session.WithUnsavedWork(OpenDocument.ListFrom(inspect.Value))
            : [];
        if (unsaved.Count == 0 && !(!inspect.Ok && _session.IsDirty))
        {
            return true;
        }

        string names = unsaved.Count > 0
            ? string.Join(", ", unsaved.Select(document => document.Name ?? document.Title))
            : _session.CurrentName ?? _untitled;
        MessageBoxDialogResult answer = await MessageBox(
            "Close Craft Fixture", $"Save the changes to {names}?", MessageBoxType.YesNoCancel, cancellationToken);
        return answer.Result switch
        {
            MessageBoxResult.Yes => inspect.Ok && await SaveAllAsync(frame, unsaved, cancellationToken),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    // After `ready`: the window's file (after Retry), else the file asked for, else the app's blank document.
    private async Task PrepareDocumentAsync()
    {
        // Runs after `ready`, a [JSInvokable] callback: the app's scope must be entered for KazoFile.
        using IDisposable scope = KazoExecutionContext.Enter(AppContext);
        if (_frame is { } frame)
        {
            FrameCommandResult inspect = await frame.RequestAsync("ui.inspect", cancellationToken: AppContext.CancellationToken);
            _untitled = (inspect.Ok ? TitleOf(inspect.Value) : null) ?? _untitled;
        }

        string? path = (_session.CurrentName is { } current ? _session.PathOf(current) : null) ?? _request.Path;
        if (path is not null)
        {
            await OpenFileHereAsync(path);
        }

        await UpdateTitleAsync();
    }

    // Reads a file and sends it to the Craft app; a file that does not read leaves a message.
    private async Task<bool> OpenFileHereAsync(string path)
    {
        if (_frame is not { } frame)
        {
            return false;
        }

        string name = CraftDocumentFormats.FileNameOf(path);
        byte[] bytes;
        try
        {
            bytes = await KazoFile.ReadAllBytesAsync(path, AppContext.CancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            await ShowNoticeAsync($"{name} could not be opened: {exception.Message}", error: true);
            return false;
        }

        if (!await frame.OpenAsync(name, bytes))
        {
            await ShowNoticeAsync($"{name} could not be opened: the app is not connected.", error: true);
            return false;
        }

        _session.Opened(name, path);
        await UpdateTitleAsync();
        return true;
    }

    private async Task OnDirtyChangedAsync(bool dirty)
    {
        using IDisposable scope = KazoExecutionContext.Enter(AppContext);
        bool before = _session.IsDirty;
        _session.SetFrameDirty(dirty);
        if (before != _session.IsDirty)
        {
            await UpdateTitleAsync();
        }
    }

    // F12 or Ctrl+Shift+S: the window asks the Craft app for its document, then treats that save as Save As.
    private async Task SaveAsAsync()
    {
        if (_frame is not { } frame || _startup.State != FrameState.Running)
        {
            return;
        }

        _saveAsRequested = true;
        FrameCommandResult started = await frame.RequestAsync("file.save", cancellationToken: AppContext.CancellationToken);
        if (!started.Ok)
        {
            _saveAsRequested = false;
            await ShowNoticeAsync($"Save As failed: {started.Error}", error: true);
        }
    }

    // One file the Craft app saves: decided by the session, written here. The answer tells the
    // proxy whether to send `saved` back to the app (its save carried an id).
    private async Task<bool> OnSavingAsync(FrameSave save)
    {
        // Called from JavaScript ([JSInvokable]): the app's scope must be entered for KazoFile.
        using IDisposable scope = KazoExecutionContext.Enter(AppContext);
        await _saveGate.WaitAsync(AppContext.CancellationToken);
        bool written = false;
        try
        {
            bool saveAs = save.SaveAs || _saveAsRequested;
            _saveAsRequested = false;
            written = !string.IsNullOrEmpty(save.Name) && await CarryOutAsync(_session.Decide(save.Name, saveAs), save.Bytes);
        }
        finally
        {
            _saveGate.Release();
            Interlocked.Exchange(ref _nextSave, null)?.TrySetResult(written);
            await UpdateTitleAsync();
        }

        return written;
    }

    // Asks for the path when the decision says so, writes the bytes, and records the outcome.
    private async Task<bool> CarryOutAsync(SaveDecision decision, byte[] bytes)
    {
        string? path;
        try
        {
            path = decision.AsksForPath ? await AskSavePathAsync(decision) : decision.Path;
            if (path is not null)
            {
                await KazoFile.WriteAllBytesAsync(path, bytes, AppContext.CancellationToken);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            _session.SaveFailed(decision);
            await ShowNoticeAsync($"{decision.Name} was not saved: {exception.Message}", error: true);
            return false;
        }

        if (path is null)
        {
            _session.SaveFailed(decision);
            await ShowNoticeAsync($"{decision.Name} was not saved.", error: false);
            return false;
        }

        _session.Saved(decision, path);
        string fileName = CraftDocumentFormats.FileNameOf(path);
        if (decision.Action == SaveAction.AskForCopy)
        {
            await ShowNoticeAsync($"Copy saved: {fileName}", error: false);
            return true;
        }

        await ClearNoticeAsync();
        if (decision.Action == SaveAction.AskAndBind && !string.Equals(decision.Name, fileName, StringComparison.Ordinal)
            && _frame is { } frame
            && (await frame.RequestAsync("doc.rename", new { name = fileName }, cancellationToken: AppContext.CancellationToken)).Ok)
        {
            // The Craft app's document takes the file's name: its next saves carry it.
            _session.Renamed(decision.Name, fileName);
        }

        return true;
    }

    // The OS save dialog, in the document's folder, for the format the Craft app chose.
    private async Task<string?> AskSavePathAsync(SaveDecision decision)
    {
        string extension = CraftDocumentFormats.ExtensionOf(decision.Name);
        CraftDocumentFormat? format = CraftFixtureApp.Formats.Find(decision.Name);
        SaveFileDialogResult result = await SaveFileAsync(new SaveFileDialogRequest
        {
            Title = decision.Action == SaveAction.AskForCopy ? "Save a copy" : "Save the document",
            InitialDirectory = VirtualPath.Parse(decision.Folder ?? AppContext.HomeDirectory),
            SuggestedFileName = decision.Name,
            DefaultExtension = extension.Length > 0 ? extension : null,
            Filters = extension.Length > 0
                ? [new FileDialogFilter($"{extension} files", [extension], format is null ? [] : [format.MediaType])]
                : [],
            ConfirmOverwrite = true,
        });
        return result.Status == FileDialogStatus.Selected && result.Resource is { } chosen
            ? CraftDocumentFormats.WithExtensionOf(chosen.Path.Value, decision.Name)
            : null;
    }

    // "Yes" in the close dialog: each document is saved in turn; the window closes only when all
    // of them reached a file.
    private async Task<bool> SaveAllAsync(CraftFrame frame, IReadOnlyList<OpenDocument> documents, CancellationToken cancellationToken)
    {
        foreach (OpenDocument document in documents)
        {
            TaskCompletionSource<bool> outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _nextSave = outcome;
            FrameCommandResult started = await frame.RequestAsync("file.save", new { doc = document.Id }, cancellationToken: cancellationToken);
            if (!started.Ok || !await outcome.Task.WaitAsync(cancellationToken))
            {
                _nextSave = null;
                return false;
            }
        }

        return true;
    }

    private async Task UpdateTitleAsync()
    {
        try
        {
            await SetWindowTitleAsync(DocumentTitle);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JSDisconnectedException)
        {
            // The window is closing.
        }

        await InvokeAsync(StateHasChanged);
    }

    private async Task ShowNoticeAsync(string text, bool error)
    {
        _notice = text;
        _noticeIsError = error;
        await InvokeAsync(StateHasChanged);
    }

    private async Task ClearNoticeAsync()
    {
        if (_notice is not null)
        {
            _notice = null;
            await InvokeAsync(StateHasChanged);
        }
    }
}
