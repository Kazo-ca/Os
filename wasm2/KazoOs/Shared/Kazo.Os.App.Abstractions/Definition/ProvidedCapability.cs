namespace Kazo.Os.App.Abstractions;

/// <summary>
/// One permission a <see cref="FeatureDefinition"/> declares (ADR 0246 §1): what its services and
/// commands require of the apps that call them, with the same rules as the OS catalog
/// (<see cref="AppCapabilities"/>): a normal one is granted at sign-in, a dangerous one only after an
/// administrator's approval, and never silently.
/// </summary>
/// <param name="Id">The identifier, under the feature's <see cref="FeatureDefinition.CapabilityPrefix"/>: <c>x86.programs.launch</c>.</param>
/// <param name="Risk">Normal or dangerous.</param>
/// <param name="Title">The short title the approval window and Settings show.</param>
public sealed record ProvidedCapability(string Id, CapabilityRisk Risk, string Title)
{
    /// <summary>Gets the sentence that explains the risk to an administrator; required for a dangerous permission.</summary>
    public string? RiskStatement { get; init; }

    /// <summary>
    /// Gets the permissions of the same feature this one covers (ADR 0246 §3): granting
    /// <c>x86.vm</c> grants <c>x86.vm.use</c> and <c>x86.vm.destroy</c>. A covering permission is at
    /// least as dangerous as what it covers.
    /// </summary>
    public IReadOnlyList<string> Covers { get; init; } = [];
}
