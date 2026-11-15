namespace Kazo.Os.Apps.Store;

/// <summary>
/// The order in which an app's screenshots are shown (ADR 0266 §8): on a phone, the screenshots
/// taken in portrait come before those taken in landscape, which would be tiny on a narrow screen.
/// </summary>
/// <remarks>
/// <para>
/// The catalog does not give a screenshot's size, and the decision keeps the catalog as it is: the
/// sizes are read in the browser once the images are known (<c>StoreScreenshots.razor.js</c>). A
/// screenshot whose size is not known yet counts as landscape, so it stays where the catalog put it
/// until its size arrives.
/// </para>
/// <para>
/// The order is stable: inside the portrait group and inside the landscape group the catalog's
/// order is kept, because the publisher chose it.
/// </para>
/// </remarks>
internal static class StoreScreenshotOrder
{
    /// <summary>Tells whether a screenshot of this size is taken in portrait (taller than wide).</summary>
    /// <param name="size">The size read in the browser, or <see langword="null"/> while it is not known.</param>
    internal static bool IsPortrait(StoreScreenshotSize? size) =>
        size is { Width: > 0, Height: > 0 } known && known.Height > known.Width;

    /// <summary>
    /// Gives, for each place in the strip, the index of the screenshot in the catalog's list.
    /// </summary>
    /// <param name="sizes">The size of each screenshot, in the catalog's order; <see langword="null"/> when not known.</param>
    /// <param name="portraitFirst">Whether the portrait screenshots go first (a phone).</param>
    /// <returns>The catalog's indexes in the order to show them.</returns>
    internal static int[] Arrange(IReadOnlyList<StoreScreenshotSize?> sizes, bool portraitFirst)
    {
        ArgumentNullException.ThrowIfNull(sizes);
        int[] identity = [.. Enumerable.Range(0, sizes.Count)];
        if (!portraitFirst)
        {
            return identity;
        }

        // OrderBy is a stable sort: the catalog's order stays inside each group.
        return [.. identity.OrderBy(index => IsPortrait(sizes[index]) ? 0 : 1)];
    }
}

/// <summary>The size of a screenshot in pixels, as the browser read it.</summary>
/// <param name="Width">The image's own width.</param>
/// <param name="Height">The image's own height.</param>
internal readonly record struct StoreScreenshotSize(double Width, double Height);
