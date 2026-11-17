using System.Globalization;
using Kazo.Os.Platform.Core.Storage;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Kazo.Os.Ecosystem;

/// <summary>
/// ADR 0250 §2: tells the user the browser erased Kazo OS's data, with the date of the last boot and
/// the way back (restoring a backup of ADR 0099 once the new account exists). The OS goes on as a new
/// installation; nothing on this notice blocks it.
/// </summary>
public sealed partial class StorageEvictionNotice
{
    /// <summary>Gets or sets the eviction the boot found.</summary>
    [Parameter]
    [EditorRequired]
    public StorageEviction Eviction { get; set; } = default!;

    /// <summary>Gets or sets the host's texts.</summary>
    [Inject]
    private IStringLocalizer<Strings> L { get; set; } = default!;

    private string LastBootText => Eviction.LastBootUtc is { } lastBoot
        ? lastBoot.ToLocalTime().ToString("f", CultureInfo.CurrentUICulture)
        : L["StorageEvicted.LastBootUnknown"];
}
