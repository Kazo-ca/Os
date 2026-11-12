using System.Globalization;
using System.Resources;
using Kazo.Os.AppSdk.Blazor;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kazo.Os.Fixtures.InstalledAssets;

/// <summary>
/// Imports <c>probe.js</c> from the app's own <c>_content/</c> address, which starts a Web Worker
/// that loads <c>engine.js</c> next to it; shows what the worker answered (ADR 0206). Also imports
/// its collocated <c>ProbeWindow.razor.js</c>, which only <c>KazoOsPack</c> puts in the package
/// (ADR 0221). Shows its French greeting, read from the satellite assembly
/// <c>lib/fr/Kazo.Os.Fixtures.InstalledAssets.resources.dll</c> of the package (ADR 0222).
/// Calls CoreLib overloads the OS itself does not use, which trimming removed before ADR 0242:
/// without CoreLib kept whole, the install check refuses this package.
/// Calls <c>Kazo.Fixtures.InstalledLibrary</c>, a library of the app the package carries in
/// <c>lib/</c> (ADR 0244 §1): the OS loads it before this assembly.
/// </summary>
public partial class ProbeWindow : WindowAppBase
{
    private string _status = "starting";
    private string _collocated = "starting";

    // ADR 0222: French whatever the user's language, so the test does not depend on the session's.
    private readonly string _french = ReadFrenchGreeting();

    // ADR 0242: the overloads IRC 1.0.0 and Webamp 1.1.0 call (Int32.Parse of a span,
    // String.Compare with offsets, String.Split of strings).
    private readonly string _coreLib = CallCoreLibOverloads();

    // ADR 0244 §1: a call into the second assembly of the package, a library of the app.
    private readonly string _library = Kazo.Fixtures.InstalledLibrary.ProbeLibrary.Describe();

    [Inject]
    private IJSRuntime JS { get; set; } = null!;

    /// <inheritdoc />
    protected override async Task OnAppAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        try
        {
            await using IJSObjectReference module = await JS.InvokeAsync<IJSObjectReference>(
                "import", "./_content/Kazo.Os.Fixtures.InstalledAssets/probe.js");
            _status = await module.InvokeAsync<string>("run");
        }
        catch (JSException exception)
        {
            _status = $"failed: {exception.Message}";
        }

        try
        {
            await using IJSObjectReference collocated = await JS.InvokeAsync<IJSObjectReference>(
                "import", "./_content/Kazo.Os.Fixtures.InstalledAssets/ProbeWindow.razor.js");
            _collocated = await collocated.InvokeAsync<string>("label");
        }
        catch (JSException exception)
        {
            _collocated = $"failed: {exception.Message}";
        }

        StateHasChanged();
    }

    private static string CallCoreLibOverloads()
    {
        int number = int.Parse("x42".AsSpan(1), CultureInfo.InvariantCulture);
        int compared = string.Compare("xKAZO", 1, "kazo", 0, 4, StringComparison.OrdinalIgnoreCase);
        int lines = "a\r\nb".Split(["\r\n", "\n"], StringSplitOptions.None).Length;
        return $"corelib {number} {compared} {lines}";
    }

    private static string ReadFrenchGreeting()
    {
        try
        {
            ResourceManager strings = new("Kazo.Os.Fixtures.InstalledAssets.Resources.ProbeStrings", typeof(ProbeWindow).Assembly);
            return strings.GetString("Greeting", CultureInfo.GetCultureInfo("fr")) ?? "missing";
        }
        catch (Exception exception) when (exception is MissingManifestResourceException or CultureNotFoundException)
        {
            return $"failed: {exception.Message}";
        }
    }
}
