using Kazo.Os.App.Abstractions;
using Kazo.Os.AppSdk.Administration;
using Kazo.Os.AppSdk.UserEvents;

namespace Kazo.Os.Apps.ErrorLogViewer;

/// <summary>
/// The "My events" tab of the viewer (ADR 0254, section 3): the journal of the signed-in user, read as
/// an ordinary file the user owns; an administrator also picks another account, whose journal the OS
/// reads after its own authentication, once for this viewer (<see cref="KazoUserAccounts.ReadEventsAsync(Kazo.Os.AppSdk.IAppExecutionContext, string, KazoUserEventFilter?, CancellationToken)"/>).
/// </summary>
public partial class ErrorLogViewerWindow
{
    private IReadOnlyList<KazoUserEventEntry> _userEvents = [];
    private IReadOnlyList<KazoUserAccount> _accounts = [];

    /// <summary>Gets or sets the account whose journal is shown; empty for the signed-in user.</summary>
    private string SelectedAccount { get; set; } = string.Empty;

    /// <summary>Gets whether the user picker is shown: for an administrator, or under <c>sudo</c>.</summary>
    private bool CanPickAccount =>
        AppContext.IsRootElevated || AppContext.UserAuthority >= AppAuthority.Administrator;

    /// <summary>
    /// Reads the journal shown: the signed-in user's with the viewer's own file rights, or another
    /// account's through the OS (an administrator, asked once per viewer).
    /// </summary>
    private async Task ReloadUserEventsAsync()
    {
        if (CanPickAccount && _accounts.Count == 0)
        {
            _accounts = await KazoUserAccounts.ListAsync(AppContext, _lifetime.Token);
        }

        if (SelectedAccount.Length > 0 && !string.Equals(SelectedAccount, AppContext.UserLoginName, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _userEvents = await KazoUserAccounts.ReadEventsAsync(
                    AppContext, SelectedAccount, new KazoUserEventFilter { Limit = KazoUserEventFilter.MaximumLimit }, _lifetime.Token);
                return;
            }
            catch (UnauthorizedAccessException)
            {
                // No administrator answered: back to the user's own journal, and say why.
                _errorMessage = L["Error.EventsNeedAdmin", SelectedAccount];
                SelectedAccount = string.Empty;
            }
        }

        UserEventsContent content = await _reader.ReadUserEventsAsync(AppContext.UserId, _lifetime.Token);
        _userEvents = content.Entries;
        if (content.Error is { } error && _errorMessage is null)
        {
            _errorMessage = L["Error.CannotRead", error.Path?.Value ?? LogFileReader.UserLogDirectory.Value, error.Code];
        }
    }

    private async Task OnAccountChangedAsync()
    {
        _errorMessage = null;
        await ReloadUserEventsAsync();
    }
}
