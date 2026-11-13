using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>
/// The capabilities of a gateway the OS uses itself rather than an app (ADR 0246 §3): exactly the
/// ones listed, whatever the authority asked; any other is refused as missing.
/// </summary>
/// <param name="granted">The capabilities it holds.</param>
internal sealed class OsCapabilityChecker(params string[] granted) : ICapabilityChecker
{
    /// <inheritdoc />
    /// <remarks>
    /// The management gateway only calls <see cref="Require"/>; an evaluation has no grant record to
    /// show, so it is not offered rather than invented.
    /// </remarks>
    public CapabilityPolicyEvaluation Evaluate(
        string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null) =>
        throw new NotSupportedException("The OS's own gateways only require capabilities.");

    /// <inheritdoc />
    public void Require(
        string capability, AppAuthority requiredAuthority = AppAuthority.User, CapabilityResourceCandidate? resourceCandidate = null)
    {
        if (!granted.Contains(capability, StringComparer.Ordinal))
        {
            throw new AppGatewayAccessDeniedException(capability, CapabilityPolicyEvaluation.DenyMissing(1));
        }
    }
}
