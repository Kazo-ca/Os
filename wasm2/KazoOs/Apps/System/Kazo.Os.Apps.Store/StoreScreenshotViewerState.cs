namespace Kazo.Os.Apps.Store;

/// <summary>
/// Which of an app's screenshots the Store's viewer shows, if any: the viewer's whole state, kept as
/// an immutable value so every move (open, next, previous, close, a swipe) is a plain function that
/// the tests check without a browser.
/// </summary>
/// <param name="Count">The number of screenshots of the app's page.</param>
/// <param name="Index">The screenshot shown, from 0, or <see langword="null"/> while the viewer is closed.</param>
internal readonly record struct StoreScreenshotViewerState(int Count, int? Index)
{
    /// <summary>
    /// The shortest horizontal move, in CSS pixels, that counts as a swipe. Shorter moves are taps or
    /// a hand that shook; 48 px is about the width of a fingertip target.
    /// </summary>
    internal const double SwipeThreshold = 48;

    /// <summary>Gets a value indicating whether the viewer shows a screenshot.</summary>
    internal bool IsOpen => Index is not null;

    /// <summary>Gets a value indicating whether a screenshot comes before the one shown.</summary>
    internal bool HasPrevious => Index is > 0;

    /// <summary>Gets a value indicating whether a screenshot comes after the one shown.</summary>
    internal bool HasNext => Index is { } index && index < Count - 1;

    /// <summary>The closed viewer of a page with <paramref name="count"/> screenshots.</summary>
    /// <param name="count">The number of screenshots; a negative number counts as none.</param>
    internal static StoreScreenshotViewerState Closed(int count) => new(Math.Max(0, count), null);

    /// <summary>Opens the viewer on a screenshot; an index outside the list leaves it as it is.</summary>
    /// <param name="index">The screenshot to show, from 0.</param>
    internal StoreScreenshotViewerState Open(int index) =>
        index >= 0 && index < Count ? this with { Index = index } : this;

    /// <summary>Shows the next screenshot. On the last one, nothing moves: the list does not loop.</summary>
    internal StoreScreenshotViewerState Next() => HasNext ? this with { Index = Index + 1 } : this;

    /// <summary>Shows the previous screenshot. On the first one, nothing moves.</summary>
    internal StoreScreenshotViewerState Previous() => HasPrevious ? this with { Index = Index - 1 } : this;

    /// <summary>Closes the viewer.</summary>
    internal StoreScreenshotViewerState Close() => this with { Index = null };

    /// <summary>
    /// Applies a finger (or pen, or mouse) move across the image: towards the left shows the next
    /// screenshot, as when turning a page, towards the right the previous one. A move shorter than
    /// <see cref="SwipeThreshold"/> or more vertical than horizontal changes nothing.
    /// </summary>
    /// <param name="deltaX">The horizontal move, positive to the right.</param>
    /// <param name="deltaY">The vertical move, positive downwards.</param>
    internal StoreScreenshotViewerState Swipe(double deltaX, double deltaY)
    {
        if (!IsSwipe(deltaX, deltaY))
        {
            return this;
        }

        return deltaX < 0 ? Next() : Previous();
    }

    /// <summary>Tells whether a pointer move is a horizontal swipe rather than a tap.</summary>
    /// <param name="deltaX">The horizontal move.</param>
    /// <param name="deltaY">The vertical move.</param>
    internal static bool IsSwipe(double deltaX, double deltaY) =>
        double.IsFinite(deltaX) && double.IsFinite(deltaY)
        && Math.Abs(deltaX) >= SwipeThreshold && Math.Abs(deltaX) > Math.Abs(deltaY);

    /// <summary>
    /// Applies a key pressed while the viewer has the focus: ← and → move, Home and End jump to the
    /// ends, Escape closes. Other keys change nothing (Tab keeps moving the focus).
    /// </summary>
    /// <param name="key">The key's <c>KeyboardEvent.key</c> value.</param>
    internal StoreScreenshotViewerState Key(string? key) => key switch
    {
        "ArrowRight" => Next(),
        "ArrowLeft" => Previous(),
        "Home" when IsOpen && Count > 0 => this with { Index = 0 },
        "End" when IsOpen && Count > 0 => this with { Index = Count - 1 },
        "Escape" => Close(),
        _ => this,
    };
}
