using System.Globalization;
using System.Reflection;

namespace Kazo.Os.Platform.Blazor.LazyLoading;

/// <summary>
/// The build number of the running OS (ADR 0236): the number of commits of <c>main</c> that the
/// CI passes as <c>-p:KazoOsBuildNumber=…</c> when it publishes the OS. The build writes it as an
/// <see cref="AssemblyMetadataAttribute"/> of the host's entry assembly and in <c>version.json</c>.
/// A local build has none: the screens then say « dev ».
/// </summary>
public static class HostBuildNumber
{
    /// <summary>The key of the <see cref="AssemblyMetadataAttribute"/> that carries the number.</summary>
    public const string MetadataKey = "KazoOsBuildNumber";

    /// <summary>Reads the build number written on <paramref name="assembly"/>, or <see langword="null"/> when it has none.</summary>
    /// <param name="assembly">The host's entry assembly (<see cref="Assembly.GetEntryAssembly"/>).</param>
    public static int? Read(Assembly? assembly) => Kazo.Os.App.Abstractions.KazoOsSdk.ReadBuildNumber(assembly);

    /// <summary>
    /// Returns the build number in <paramref name="value"/>: a positive integer. An empty value,
    /// zero or text (a local build) is no number.
    /// </summary>
    /// <param name="value">The metadata value, for example <c>1234</c>.</param>
    public static int? Parse(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0
            ? number
            : null;
}
