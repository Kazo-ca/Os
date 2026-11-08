namespace Kazo.Os.AppSdk.Blazor.Components;

/// <summary>
/// The badge <see cref="KazoOsVersionLabel"/> shows next to the version (ADR 0236): whether the
/// running Kazo OS is the one its origin serves now. The host that knows it (the sign-in screen)
/// passes it; an app that does not know leaves <see cref="None"/>.
/// </summary>
public enum KazoOsVersionStatus
{
    /// <summary>No badge.</summary>
    None = 0,

    /// <summary>« Checking… »: no answer from the origin yet.</summary>
    Checking,

    /// <summary>« Up to date »: the origin serves this version and build.</summary>
    UpToDate,

    /// <summary>« Outdated »: the origin serves another version or build.</summary>
    Outdated,

    /// <summary>« Status unknown (offline) »: the origin did not answer.</summary>
    Unreachable,
}
