namespace Kazo.Os.Apps.Store;

/// <summary>
/// What the Store's command line asks for: a tab (<c>--page=installed</c>, <c>--page=themes</c>), an
/// app's page (<c>--app=&lt;appId&gt;</c>) and the file to open once that app is installed
/// (<c>--open-after-install=&lt;path&gt;</c>, ADR 0257 §5). The same arguments reach a Store already
/// open, through the launch intent the OS delivers to its single window.
/// </summary>
/// <param name="View">The page to show, or <see langword="null"/> to keep the current one.</param>
/// <param name="AppId">The app whose page to show, with <see cref="StoreView.Detail"/>.</param>
/// <param name="OpenAfterInstall">
/// The file to open once <see cref="AppId"/> is installed, or <see langword="null"/>. Only kept with an
/// app: the Store never opens a file after installing anything else.
/// </param>
internal sealed record StoreLaunchArguments(StoreView? View, string? AppId, string? OpenAfterInstall)
{
    /// <summary>Prefix of the tab argument.</summary>
    internal const string PageArgument = "--page=";

    /// <summary>Prefix of the app page argument.</summary>
    internal const string AppArgument = "--app=";

    /// <summary>Prefix of the file to open after the install (ADR 0257 §5).</summary>
    internal const string OpenAfterInstallArgument = "--open-after-install=";

    /// <summary>Reads the arguments; unknown ones are ignored and, for each kind, the last one wins.</summary>
    /// <param name="arguments">The command line.</param>
    internal static StoreLaunchArguments Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        StoreView? view = null;
        string? appId = null;
        string? openAfterInstall = null;
        foreach (string argument in arguments)
        {
            if (argument.StartsWith(PageArgument, StringComparison.Ordinal))
            {
                view = argument[PageArgument.Length..] switch
                {
                    "installed" => StoreView.Installed,
                    "themes" => StoreView.Themes,
                    _ => view
                };
            }
            else if (argument.StartsWith(AppArgument, StringComparison.Ordinal) && argument.Length > AppArgument.Length)
            {
                appId = argument[AppArgument.Length..].Trim();
                view = StoreView.Detail;
            }
            else if (argument.StartsWith(OpenAfterInstallArgument, StringComparison.Ordinal)
                && argument[OpenAfterInstallArgument.Length..].Trim() is { Length: > 0 } path
                && path.StartsWith('/'))
            {
                openAfterInstall = path;
            }
        }

        return new StoreLaunchArguments(view, appId, appId is null ? null : openAfterInstall);
    }
}
