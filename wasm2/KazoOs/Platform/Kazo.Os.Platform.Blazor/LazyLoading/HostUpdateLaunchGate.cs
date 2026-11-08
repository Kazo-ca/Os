using System.Globalization;
using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Lifecycle;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Platform.Blazor.LazyLoading;

/// <summary>
/// ADR 0239 §2: an app the user opens starts only when the running Kazo OS is the version its
/// origin serves. Otherwise the launch is refused with « Une nouvelle version de Kazo OS est
/// disponible. Redémarrez pour ouvrir « App ». » and <see cref="UpdateRequired"/> lets the host show
/// the restart dialog: an app loaded from an outdated copy could mix files of two versions (the
/// crash of the IRC app on 2026-11-08, ADR 0237).
/// </summary>
/// <remarks>
/// The gate reads <c>version.json</c> again when the last answer is older than
/// <see cref="RecheckInterval"/>, waiting at most <see cref="CheckTimeout"/>; an origin that does
/// not answer lets the app start (offline use). An update already known refuses at once.
/// </remarks>
public sealed class HostUpdateLaunchGate : IAppLaunchGate
{
    /// <summary>The time during which an answer of the origin is reused.</summary>
    public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>How long a launch waits for the origin before starting anyway.</summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(3);

    private readonly HostReleaseMonitor _monitor;
    private readonly IStringLocalizer _texts;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private DateTimeOffset? _checkedAt;

    /// <summary>Creates the gate.</summary>
    /// <param name="monitor">Reads the served version.</param>
    /// <param name="culture">Supplies the session language of the refusal text.</param>
    /// <param name="time">The clock; <see cref="TimeProvider.System"/> when <see langword="null"/>.</param>
    public HostUpdateLaunchGate(HostReleaseMonitor monitor, Func<CultureInfo> culture, TimeProvider? time = null)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _texts = Strings.For(culture ?? throw new ArgumentNullException(nameof(culture)));
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised with the app's name each time a launch is refused, so the host offers the restart.</summary>
    public event Action<string>? UpdateRequired;

    /// <inheritdoc />
    public async ValueTask<AppLaunchRefusal?> CheckAsync(AppManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (_monitor.Status != HostReleaseStatus.UpdateAvailable && IsStale())
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(CheckTimeout);
            try
            {
                await _monitor.CheckAsync(timeout.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    _checkedAt = _time.GetUtcNow();
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The origin is slow: the app starts, the next launch asks again.
            }
        }

        if (_monitor.Status != HostReleaseStatus.UpdateAvailable)
        {
            return null;
        }

        string name = string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name;
        UpdateRequired?.Invoke(name);
        return new AppLaunchRefusal(AppLaunchRefusal.UpdateRequiredCode, _texts["UpdateRequired.Message", name]);
    }

    private bool IsStale()
    {
        lock (_gate)
        {
            return _checkedAt is not { } checkedAt || _time.GetUtcNow() - checkedAt >= RecheckInterval;
        }
    }
}
