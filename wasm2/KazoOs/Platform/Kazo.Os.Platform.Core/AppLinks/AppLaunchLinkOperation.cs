namespace Kazo.Os.Platform.Core.AppLinks;

/// <summary>Step of a direct-link launch that the progress window shows (ADR 0258 §3).</summary>
public enum AppLaunchLinkStep
{
    /// <summary>The app is looked up in the official catalog.</summary>
    Looking = 0,

    /// <summary>The package is downloaded, checked and installed for the session user.</summary>
    Installing = 1,

    /// <summary>The app is started.</summary>
    Launching = 2,
}

/// <summary>
/// The direct-link launch in progress, as the OS's progress window shows it (ADR 0258 §3): the app,
/// what the OS is doing, and the « Annuler » button. Immutable: the runner replaces it at each step.
/// </summary>
/// <param name="AppId">The app of the link.</param>
/// <param name="Name">Its name in the catalog, or its id until the catalog was read.</param>
/// <param name="Publisher">Who publishes it (the owner of its repository), or <see langword="null"/> when unknown.</param>
/// <param name="Version">The version installed, or <see langword="null"/> until known.</param>
/// <param name="Step">What the OS is doing.</param>
public sealed record AppLaunchLinkOperation(string AppId, string Name, string? Publisher, string? Version, AppLaunchLinkStep Step)
{
    /// <summary>Number of steps, for the progress bar.</summary>
    public const int StepCount = 3;

    /// <summary>Gets the progress in percent shown by the bar: the share of the steps already started.</summary>
    public int Percent => ((int)Step + 1) * 100 / StepCount;

    /// <summary>Gets whether « Annuler » still stops something: once the app starts, there is nothing left to cancel.</summary>
    public bool CanCancel => Step != AppLaunchLinkStep.Launching;
}
