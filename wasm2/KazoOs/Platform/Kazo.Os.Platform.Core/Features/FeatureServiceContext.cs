using Kazo.Os.AppSdk;
using Kazo.Os.AppSdk.Features;

namespace Kazo.Os.Platform.Core.Features;

/// <summary>
/// The context a feature's service is created with (ADR 0262 §1): the feature's own execution
/// context, the app whose call runs now, and the page's stop token.
/// </summary>
internal sealed class FeatureServiceContext : IFeatureServiceContext
{
    // The app that called before EnterFeature made the feature's context ambient.
    private static readonly AsyncLocal<KazoFeatureCaller?> s_enteredFrom = new();

    /// <summary>Creates the context.</summary>
    /// <param name="feature">The feature's execution context.</param>
    /// <param name="stopping">Cancelled when the page stops or the feature leaves.</param>
    public FeatureServiceContext(IAppExecutionContext feature, CancellationToken stopping)
    {
        Feature = feature ?? throw new ArgumentNullException(nameof(feature));
        Stopping = stopping;
    }

    /// <inheritdoc />
    public IAppExecutionContext Feature { get; }

    /// <inheritdoc />
    public CancellationToken Stopping { get; }

    /// <inheritdoc />
    public KazoFeatureCaller? Caller => s_enteredFrom.Value ?? Describe(KazoExecutionContext.CurrentOrNull);

    /// <inheritdoc />
    public IDisposable EnterFeature()
    {
        KazoFeatureCaller? caller = Caller;
        KazoFeatureCaller? previous = s_enteredFrom.Value;
        s_enteredFrom.Value = caller;
        IDisposable ambient = KazoExecutionContext.Enter(Feature);
        return new Scope(() =>
        {
            ambient.Dispose();
            s_enteredFrom.Value = previous;
        });
    }

    /// <summary>The caller an ambient context names; the feature itself is no caller.</summary>
    private KazoFeatureCaller? Describe(IAppExecutionContext? current) =>
        current is null || ReferenceEquals(current, Feature)
            ? null
            : new KazoFeatureCaller(current.Manifest.Id, current.ProcessId.Value, current.UserId);

    private sealed class Scope(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                dispose();
            }
        }
    }
}
