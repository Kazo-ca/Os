namespace Kazo.Os.AppSdk.Testing.Drives;

/// <summary>How one case of <see cref="KazoDriveProviderTests"/> ended, when it did not fail.</summary>
public enum KazoDriveConformanceOutcome
{
    /// <summary>The provider behaved as the contract asks.</summary>
    Passed,

    /// <summary>
    /// The case could not run: the test class does not simulate what it needs (the network going
    /// down, an expired sign-in, a file larger than the limit), or the service keeps no revision.
    /// <see cref="KazoDriveConformanceResult.Detail"/> says why.
    /// </summary>
    Skipped
}

/// <summary>The end of one case of <see cref="KazoDriveProviderTests"/> that did not fail.</summary>
/// <param name="Case">The case's name, one of <see cref="KazoDriveProviderTests.CaseNames"/>.</param>
/// <param name="Outcome">Passed or skipped.</param>
/// <param name="Detail">Why the case was skipped, or a note about how it passed; <see langword="null"/> when there is nothing to say.</param>
public sealed record KazoDriveConformanceResult(string Case, KazoDriveConformanceOutcome Outcome, string? Detail = null);

/// <summary>A case of <see cref="KazoDriveProviderTests"/> found the provider breaking the contract (ADR 0282 §2).</summary>
public sealed class KazoDriveConformanceException : Exception
{
    /// <summary>Initializes the exception.</summary>
    /// <param name="caseName">The case that failed.</param>
    /// <param name="message">What the provider did and what the contract asks.</param>
    /// <param name="innerException">The provider's exception, when it threw the wrong one.</param>
    public KazoDriveConformanceException(string caseName, string message, Exception? innerException = null)
        : base($"{caseName}: {message}", innerException)
    {
        Case = caseName;
    }

    /// <summary>Gets the case that failed.</summary>
    public string Case { get; }
}
