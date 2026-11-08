namespace Kazo.Os.Platform.Core.Apps.Packages;

/// <summary>
/// Corrects the app repositories of Kazo-ca that a profile created before Kazo OS 3.0.0 still names.
/// With 3.0.0 (ADR 0228), <c>Kazo-ca/HackerOs-AppMarketPlace</c> and <c>Kazo-ca/HackerOs-App-*</c>
/// became <c>Kazo-ca/Os-AppMarketPlace</c> and <c>Kazo-ca/Os-App-*</c>. The rename migration of the
/// profile (ADR 0230) rewrites every <c>HackerOs</c> as <c>KazoOs</c>, so it turned the address of
/// the official catalog in <c>/etc/kazoos/app-sources.config</c> into
/// <c>Kazo-ca/KazoOs-AppMarketPlace</c>, which answers 404: the Store then showed
/// "The catalog 'official' could not be read" and no app. Reading corrects both old spellings, so
/// a profile migrated before this fix and one migrated after it both work, without rewriting the file.
/// See doc/en/wasm/dev/rename-migration.md.
/// </summary>
internal static class RenamedAppRepositories
{
    /// <summary>The organization whose app repositories were renamed.</summary>
    private const string Organization = "Kazo-ca";

    /// <summary>The current prefix of its app repositories (Os-AppMarketPlace, Os-AppTemplate, Os-App-*).</summary>
    private const string CurrentPrefix = "Os-App";

    /// <summary>The prefixes they had before 3.0.0, and the one the rename migration wrote by mistake.</summary>
    private static readonly string[] OldPrefixes = ["HackerOs-App", "KazoOs-App"];

    /// <summary>
    /// Gets the current name of a repository of <paramref name="owner"/>: the same name, except for
    /// an app repository of Kazo-ca under an old prefix.
    /// </summary>
    /// <param name="owner">GitHub owner.</param>
    /// <param name="repository">Repository name.</param>
    public static string Correct(string owner, string repository)
    {
        if (!string.Equals(owner, Organization, StringComparison.OrdinalIgnoreCase))
        {
            return repository;
        }

        foreach (string prefix in OldPrefixes)
        {
            if (repository.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return CurrentPrefix + repository[prefix.Length..];
            }
        }

        return repository;
    }

    /// <summary>
    /// Corrects a catalog address on GitHub (<c>https://raw.githubusercontent.com/&lt;owner&gt;/&lt;repository&gt;/…</c>
    /// or <c>https://github.com/&lt;owner&gt;/&lt;repository&gt;/…</c>). An address on another host is kept.
    /// </summary>
    /// <param name="address">The address read from the file.</param>
    public static Uri Correct(Uri address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (!string.Equals(address.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(address.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            return address;
        }

        string[] segments = address.AbsolutePath.Split('/');
        // "/owner/repository/..." splits into "", owner, repository, ...
        if (segments.Length < 3)
        {
            return address;
        }

        string repository = Correct(segments[1], segments[2]);
        if (string.Equals(repository, segments[2], StringComparison.Ordinal))
        {
            return address;
        }

        // The owner takes its canonical spelling too, so the address equals the built-in one.
        segments[1] = Organization;
        segments[2] = repository;
        return new UriBuilder(address) { Path = string.Join('/', segments) }.Uri;
    }
}
