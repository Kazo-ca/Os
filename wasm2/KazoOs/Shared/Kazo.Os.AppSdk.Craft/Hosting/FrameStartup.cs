namespace Kazo.Os.AppSdk.Craft;

/// <summary>What a Craft app window shows while the app starts.</summary>
public enum FrameState
{
    /// <summary>The Craft app is downloading or starting: the loading screen.</summary>
    Loading,

    /// <summary>The Craft app answered <c>ready</c>: it is on screen.</summary>
    Running,

    /// <summary>The Craft app reported a failure, or did not answer in time: the error screen.</summary>
    Failed,
}

/// <summary>
/// The start of the Craft app in one window: loading,
/// then the Craft app, or an error screen with "Retry" when the Craft app fails or does not answer within
/// <see cref="Timeout"/>.
/// </summary>
/// <remarks>
/// Each start attempt has a number: a timeout armed for an earlier attempt does nothing. A
/// <c>ready</c> that arrives after the timeout still puts the Craft app on screen: it was only slow
/// (a first download on a slow network), and the user would otherwise retry for nothing.
/// </remarks>
public sealed class FrameStartup
{
    /// <summary>How long the Craft app has to answer <c>ready</c> or <c>failed</c>.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>Gets what the window shows.</summary>
    public FrameState State { get; private set; } = FrameState.Loading;

    /// <summary>Gets the number of the current attempt, starting at 1.</summary>
    public int Attempt { get; private set; } = 1;

    /// <summary>Gets the Craft app's version once it is running.</summary>
    public string? AppVersion { get; private set; }

    /// <summary>Gets the error the Craft app reported; <see langword="null"/> after a timeout.</summary>
    public string? Error { get; private set; }

    /// <summary>Gets whether the failure is a timeout (no answer at all).</summary>
    public bool TimedOut { get; private set; }

    /// <summary>The Craft app answered <c>ready</c>.</summary>
    /// <param name="appVersion">The Craft app's version.</param>
    /// <returns><see langword="true"/> when the window must show the Craft app now.</returns>
    public bool OnReady(string? appVersion)
    {
        if (State == FrameState.Running || (State == FrameState.Failed && !TimedOut))
        {
            return false;
        }

        State = FrameState.Running;
        AppVersion = appVersion;
        Error = null;
        TimedOut = false;
        return true;
    }

    /// <summary>The Craft app, or the proxy loading it, reported a failure.</summary>
    /// <param name="error">The error.</param>
    /// <returns><see langword="true"/> when the window must show the error screen now.</returns>
    public bool OnFailed(string? error)
    {
        if (State == FrameState.Running)
        {
            return false;
        }

        State = FrameState.Failed;
        Error = string.IsNullOrWhiteSpace(error) ? null : error;
        TimedOut = false;
        return true;
    }

    /// <summary>The timeout of attempt <paramref name="attempt"/> elapsed.</summary>
    /// <param name="attempt">The attempt the timeout was armed for.</param>
    /// <returns><see langword="true"/> when the window must show the error screen now.</returns>
    public bool OnTimeout(int attempt)
    {
        if (attempt != Attempt || State != FrameState.Loading)
        {
            return false;
        }

        State = FrameState.Failed;
        Error = null;
        TimedOut = true;
        return true;
    }

    /// <summary>The user chose "Retry": a new attempt starts, loading again.</summary>
    /// <returns>The number of the new attempt.</returns>
    public int Retry()
    {
        Attempt++;
        State = FrameState.Loading;
        AppVersion = null;
        Error = null;
        TimedOut = false;
        return Attempt;
    }
}
