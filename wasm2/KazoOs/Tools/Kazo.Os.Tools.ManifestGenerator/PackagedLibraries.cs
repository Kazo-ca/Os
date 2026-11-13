namespace Kazo.Os.Tools.ManifestGenerator;

/// <summary>
/// The libraries <c>pack</c> puts in a package (ADR 0261 §1): the assemblies the app needs that the
/// OS does not bring, i.e. the outputs of its <c>ProjectReference</c>s and the assemblies of its NuGet
/// packages. The <c>KazoOsPack</c> target lists them (the project's copy-local references, without
/// the SDK, the .NET runtime and what the host ships) in a file, one path per line.
/// </summary>
internal static class PackagedLibraries
{
    /// <summary>Reads the libraries named by a list file.</summary>
    /// <param name="listFile">A text file holding one assembly path per line; blank lines are skipped.</param>
    /// <returns>Each library's bytes by its file name (<c>Kazo.Irc.Protocol.dll</c>), sorted.</returns>
    /// <exception cref="IOException">The list or one of its files cannot be read.</exception>
    public static SortedDictionary<string, byte[]> Read(string listFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(listFile);
        SortedDictionary<string, byte[]> libraries = new(StringComparer.Ordinal);
        foreach (string line in File.ReadAllLines(listFile))
        {
            string path = line.Trim();
            if (path.Length == 0)
            {
                continue;
            }

            // The same assembly can be listed twice (a package and a project that both bring it): one file per name.
            string fileName = Path.GetFileName(path);
            if (!libraries.ContainsKey(fileName))
            {
                libraries[fileName] = File.ReadAllBytes(path);
            }
        }

        return libraries;
    }
}
