using Kazo.Os.App.Abstractions;
using Kazo.Os.Platform.Core.Roles;
using Kazo.Os.Simulation.Abstractions.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Kazo.Os.Platform.Blazor.Shell;

/// <summary>
/// Hides from the launcher and the mobile home screen an entry point that requires a role of its
/// feature the user in session does not hold (ADR 0247 §2), as the shell leaves it out of completion.
/// The services are optional: with no session the entry point is hidden, with no role service only
/// an administrator sees it.
/// </summary>
internal static class EntryPointRoleFilter
{
    /// <summary>Gets whether <paramref name="app"/> opens to the user in session.</summary>
    /// <param name="services">The services of the component.</param>
    /// <param name="app">The entry point.</param>
    public static bool OpensToCurrentUser(IServiceProvider services, AppManifest app)
    {
        if (app.RequiresRole is null)
        {
            return true;
        }

        return services.GetService<ISessionService>()?.CurrentPrincipal is { } principal
            && FeatureRoleService.OpensTo(services.GetService<FeatureRoleService>(), app, principal);
    }
}
