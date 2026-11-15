namespace Kazo.Os.AppSdk.Features;

/// <summary>
/// The services optional features of the OS provide to apps (ADR 0262 §2), like
/// <c>GetService&lt;T&gt;()</c> of ASP.NET's dependency injection, but asynchronous: the feature's
/// code is loaded on the first request. Every member targets the app running the current code
/// (ambient context of ADR 0043).
/// </summary>
/// <remarks>
/// <para>
/// The app declares the feature in its manifest's <c>Dependencies</c> (<c>Optional = true</c> when
/// it runs without it, ADR 0262 §3); the build requires a dependency (<c>KOSAPP0034</c>).
/// </para>
/// <para>
/// No permission is checked here: each operation of the contract checks the caller's own and throws
/// <see cref="UnauthorizedAccessException"/> when one is missing, so an app tells « not installed »
/// (<see langword="null"/>) from « not allowed » (an exception).
/// </para>
/// </remarks>
public static class KazoFeatures
{
    /// <summary>
    /// Raised when a provider appears, is disabled or is removed. Subscribing and unsubscribing both
    /// need the app's ambient scope.
    /// </summary>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static event EventHandler<KazoFeatureServiceChangedEventArgs>? Changed
    {
        add => Gateway.Changed += value;
        remove => Gateway.Changed -= value;
    }

    /// <summary>
    /// Gets the instance of the feature that provides <typeparamref name="T"/>, the same for every app
    /// of the page, or <see langword="null"/> when no usable feature provides it: absent, disabled,
    /// outside the declared version range, not declared, or whose code does not load. A
    /// <see langword="null"/> is never cached: the next call tries again (a feature installed in
    /// the meantime is found). Do not keep the instance beyond its immediate use.
    /// </summary>
    /// <typeparam name="T">The contract, an interface of the feature's shared library.</typeparam>
    /// <param name="cancellationToken">Cancels the load of the feature's code.</param>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static async ValueTask<T?> GetServiceAsync<T>(CancellationToken cancellationToken = default)
        where T : class =>
        await Gateway.GetServiceAsync(typeof(T), cancellationToken).ConfigureAwait(false) as T;

    /// <summary>
    /// Gets whether a usable feature provides <typeparamref name="T"/>, without loading it: to show or
    /// hide a button.
    /// </summary>
    /// <typeparam name="T">The contract.</typeparam>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static bool IsAvailable<T>()
        where T : class => Gateway.IsAvailable(typeof(T));

    /// <summary>Gets why <typeparamref name="T"/> is, or is not, available to this app.</summary>
    /// <typeparam name="T">The contract.</typeparam>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="KazoExecutionContextMissingException">No app context is active.</exception>
    public static ValueTask<KazoFeatureServiceStatus> GetStatusAsync<T>(CancellationToken cancellationToken = default)
        where T : class => Gateway.GetStatusAsync(typeof(T), cancellationToken);

    private static IAppFeatureGateway Gateway => KazoExecutionContext.Current.Features;
}
