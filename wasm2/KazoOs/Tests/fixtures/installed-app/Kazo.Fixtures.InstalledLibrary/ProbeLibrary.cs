namespace Kazo.Fixtures.InstalledLibrary;

/// <summary>
/// What the test app of ADR 0206 calls in its second assembly (ADR 0261 §1): the answer proves the
/// library was in the package, verified at installation and loaded before the app's own code.
/// </summary>
public static class ProbeLibrary
{
    /// <summary>Gets a text only this assembly can give, with its own simple name.</summary>
    public static string Describe() => $"library ready ({typeof(ProbeLibrary).Assembly.GetName().Name})";
}
