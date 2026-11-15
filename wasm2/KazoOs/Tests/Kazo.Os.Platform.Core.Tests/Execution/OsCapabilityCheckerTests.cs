using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Execution;
using Kazo.Os.Simulation.Abstractions.Gateways;

namespace Kazo.Os.Platform.Core.Tests.Execution;

/// <summary>ADR 0258 §3: the gateway the OS uses for a direct link holds only the capabilities it was given.</summary>
public sealed class OsCapabilityCheckerTests
{
    [Fact]
    public void Requires_a_granted_capability_without_error()
    {
        // Arrange
        OsCapabilityChecker checker = new(AppCapabilities.AppsManage);

        // Act
        Exception? refused = Record.Exception(() => checker.Require(AppCapabilities.AppsManage, AppAuthority.Administrator));

        // Assert
        Assert.Null(refused);
    }

    [Fact]
    public void Refuses_any_other_capability()
    {
        // Arrange
        OsCapabilityChecker checker = new(AppCapabilities.AppsManage);

        // Act
        AppGatewayAccessDeniedException refused = Assert.Throws<AppGatewayAccessDeniedException>(
            () => checker.Require(AppCapabilities.NotificationsPost));

        // Assert
        Assert.Equal(AppCapabilities.NotificationsPost, refused.Capability);
        Assert.False(refused.Evaluation.Granted);
    }
}
