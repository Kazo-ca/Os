namespace Kazo.Os.Apps.Store;

/// <summary>
/// The Store's messages (ADR 0266 §6): the result of an action shows in a message that closes by
/// itself after <see cref="MessageLifetime"/>; an error stays until the user closes it, so it is
/// never missed. A newer message replaces the one shown and starts its own delay.
/// </summary>
public partial class StoreWindow
{
    /// <summary>How long a message that is not an error stays on screen.</summary>
    internal static readonly TimeSpan MessageLifetime = TimeSpan.FromSeconds(6);

    private string? _message;
    private bool _messageIsError;
    private CancellationTokenSource? _messageTimer;

    /// <summary>Gets the message on screen, or <see langword="null"/>.</summary>
    internal string? Message => _message;

    /// <summary>Gets whether the message on screen is an error.</summary>
    internal bool MessageIsError => _messageIsError;

    /// <summary>Whether a message closes by itself: only one that is not an error.</summary>
    /// <param name="isError">Whether the message is an error.</param>
    internal static bool ClosesByItself(bool isError) => !isError;

    /// <summary>Shows the result of an action; it closes by itself.</summary>
    /// <param name="message">The text.</param>
    internal void ShowInfo(string message) => Show(message, isError: false);

    /// <summary>Shows an error; it stays until closed.</summary>
    /// <param name="message">The text.</param>
    internal void ShowError(string message) => Show(message, isError: true);

    /// <summary>Closes the message on screen.</summary>
    internal void DismissMessage()
    {
        StopMessageTimer();
        _message = null;
        _messageIsError = false;
    }

    private void Show(string message, bool isError)
    {
        StopMessageTimer();
        _message = message;
        _messageIsError = isError;
        if (ClosesByItself(isError))
        {
            CancellationTokenSource timer = new();
            _messageTimer = timer;
            _ = CloseLaterAsync(message, timer.Token);
        }
    }

    private void StopMessageTimer()
    {
        _messageTimer?.Cancel();
        _messageTimer?.Dispose();
        _messageTimer = null;
    }

    /// <summary>Closes <paramref name="message"/> once its delay is over, if it is still the one on screen.</summary>
    private async Task CloseLaterAsync(string message, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(MessageLifetime, cancellationToken);
            await InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested && ReferenceEquals(_message, message) && !_messageIsError)
                {
                    _message = null;
                    StateHasChanged();
                }
            });
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // Replaced, closed by hand, or the window is gone: nothing to close.
        }
    }
}
