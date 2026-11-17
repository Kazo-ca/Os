using Kazo.Os.App.Abstractions;
using Kazo.Os.App.Abstractions.Policy;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Platform.Core.Execution;

/// <summary>
/// Evaluates capability policy for one bound app/user/authority by delegating to the trusted
/// grant repository, without exposing the repository itself or any other app's grants.
/// </summary>
public sealed class AppCapabilityChecker : ICapabilityChecker
{
    private readonly ICapabilityGrantRepository _repository;
    private readonly string _appId;
    private readonly string _userId;
    private readonly AppAuthority _actingAuthority;

    /// <summary>Initializes a capability checker bound to one app/user/authority.</summary>
    public AppCapabilityChecker(
        ICapabilityGrantRepository repository,
        string appId,
        string userId,
        AppAuthority actingAuthority)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        _appId = appId;
        _userId = userId;
        _actingAuthority = actingAuthority;
    }

    /// <summary>
    /// Gets the catalog of capabilities of the running OS (ADR 0246); <see langword="null"/> checks
    /// the grants alone, as before features declared permissions.
    /// </summary>
    public ICapabilityCatalog? Capabilities { get; init; }

    /// <inheritdoc />
    /// <remarks>
    /// ADR 0246 §3 and §5: a permission of a feature the catalog no longer knows (the feature was
    /// disabled or removed) is denied, whatever grant remains; a permission is also granted when a
    /// granted permission covers it (<c>x86.vm</c> covers <c>x86.vm.use</c>).
    /// </remarks>
    public CapabilityPolicyEvaluation Evaluate(
        string capability,
        AppAuthority requiredAuthority = AppAuthority.User,
        CapabilityResourceCandidate? resourceCandidate = null)
    {
        if (Capabilities is { } catalog
            && FeatureCapabilities.IsFeaturePermission(capability)
            && !catalog.IsKnown(capability))
        {
            return CapabilityPolicyEvaluation.DenyMissing(_repository.CurrentPolicyRevision);
        }

        CapabilityPolicyEvaluation evaluation = _repository.Evaluate(_appId, _userId, capability, _actingAuthority, requiredAuthority, resourceCandidate);
        if (evaluation.Granted || Capabilities is not { } covering)
        {
            return evaluation;
        }

        foreach (CapabilityDescription candidate in covering.All.Where(description => description.Covers.Count > 0))
        {
            if (covering.GetCovered(candidate.Capability).Contains(capability))
            {
                CapabilityPolicyEvaluation covered = _repository.Evaluate(_appId, _userId, candidate.Capability, _actingAuthority, requiredAuthority, resourceCandidate);
                if (covered.Granted)
                {
                    return covered;
                }
            }
        }

        return evaluation;
    }

    /// <inheritdoc />
    public void Require(
        string capability,
        AppAuthority requiredAuthority = AppAuthority.User,
        CapabilityResourceCandidate? resourceCandidate = null)
    {
        CapabilityPolicyEvaluation evaluation = Evaluate(capability, requiredAuthority, resourceCandidate);
        if (!evaluation.Granted)
        {
            throw new AppGatewayAccessDeniedException(capability, evaluation);
        }
    }
}
