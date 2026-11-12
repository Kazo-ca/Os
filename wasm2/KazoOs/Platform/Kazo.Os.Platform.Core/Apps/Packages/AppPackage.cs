using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Kazo.Os.App.Abstractions;

namespace Kazo.Os.Platform.Core.Apps.Packages;

/// <summary>Thrown when a package, its index or its contents are refused (ADR 0182 §5).</summary>
public sealed class AppPackageException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="code">Stable reason, e.g. <c>apps.package.hash-mismatch</c>.</param>
    /// <param name="message">Human-readable reason shown by <c>kazopkg</c>.</param>
    public AppPackageException(string code, string message)
        : base(message) =>
        Code = code;

    /// <summary>Gets the stable reason.</summary>
    public string Code { get; }
}

/// <summary>
/// A verified Kazo OS app package (<c>.kazoapp</c>, ADR 0182 §1): a zip archive holding
/// <c>kazoos-package.json</c>, <c>manifest.json</c>, <c>lib/*.dll</c>, the satellite assemblies
/// <c>lib/&lt;culture&gt;/&lt;assembly&gt;.resources.dll</c> of those assemblies (ADR 0222) and
/// <c>wwwroot/...</c>, where every file matches the SHA-256 the package lists.
/// </summary>
public sealed class AppPackage
{
    /// <summary>File extension of a package.</summary>
    public const string Extension = ".kazoapp";

    /// <summary>
    /// Extension of the packages published before the rename (ADR 0228 §2): still read, never written.
    /// </summary>
    public const string LegacyExtension = ".hackapp";

    /// <summary>Name of the package description at the archive root.</summary>
    public const string DescriptionFileName = "kazoos-package.json";

    /// <summary>
    /// Name of the description in a package made before the rename (ADR 0228 §2): still read, never written.
    /// </summary>
    public const string LegacyDescriptionFileName = "hackeros-package.json";

    /// <summary>Name of the app manifest at the archive root.</summary>
    public const string ManifestFileName = "manifest.json";

    /// <summary>
    /// Folder of the children of a feature (ADR 0243 §2): one <c>entrypoints/&lt;suffix&gt;.json</c>
    /// per child, each an ordinary manifest of its kind naming the feature as its <c>parent</c>, and
    /// listed under <c>manifests</c> in <see cref="DescriptionFileName"/>.
    /// </summary>
    public const string EntryPointsFolder = "entrypoints/";

    /// <summary>Folder of the app assemblies.</summary>
    public const string LibraryFolder = "lib/";

    /// <summary>File name ending of a satellite assembly (ADR 0222 §1).</summary>
    public const string SatelliteSuffix = ".resources.dll";

    /// <summary>Longest culture folder name accepted for a satellite (e.g. <c>zh-Hant-TW</c>).</summary>
    private const int MaximumCultureNameLength = 20;

    /// <summary>Folder of the assets the manifest declares.</summary>
    public const string AssetFolder = "wwwroot/";

    /// <summary>Largest archive accepted, in bytes.</summary>
    public const long MaximumArchiveBytes = 64L * 1024 * 1024;

    /// <summary>Largest total of the extracted files, in bytes.</summary>
    public const long MaximumExtractedBytes = 128L * 1024 * 1024;

    /// <summary>Most files accepted in one archive.</summary>
    public const int MaximumFiles = 2_000;

    private AppPackage(
        AppManifest manifest,
        IReadOnlyList<AppManifest> children,
        SemanticVersion version,
        IReadOnlyDictionary<string, byte[]> files,
        IReadOnlyList<AppPackageLibrary> libraries)
    {
        Manifest = manifest;
        Children = children;
        Version = version;
        Files = files;
        Libraries = libraries;
    }

    /// <summary>Gets the app identifier.</summary>
    public string AppId => Manifest.Id;

    /// <summary>Gets the package version (equal to the manifest version).</summary>
    public SemanticVersion Version { get; }

    /// <summary>Gets the app manifest: the feature's own, for a feature package.</summary>
    public AppManifest Manifest { get; }

    /// <summary>
    /// Gets the children of a feature (ADR 0243 §2), read from <c>entrypoints/*.json</c>, each
    /// validated as a manifest of its kind and against the feature; empty for any other package.
    /// </summary>
    public IReadOnlyList<AppManifest> Children { get; }

    /// <summary>Gets whether this package holds a feature (ADR 0243): installed for the whole machine only.</summary>
    public bool IsFeature => Manifest.Kind == AppKind.Feature;

    /// <summary>Gets the manifest followed by its children: every app the package installs.</summary>
    public IEnumerable<AppManifest> Manifests => Children.Prepend(Manifest);

    /// <summary>Gets every verified file by its archive path, <see cref="DescriptionFileName"/> included.</summary>
    public IReadOnlyDictionary<string, byte[]> Files { get; }

    /// <summary>
    /// Gets the libraries of the app (ADR 0244 §1), listed in <c>libraries</c> of
    /// <see cref="DescriptionFileName"/>: each one is a file of <c>lib/</c> whose name, version, SHA-256
    /// and shared mark were checked against the assembly itself. Empty for an app without a library.
    /// </summary>
    public IReadOnlyList<AppPackageLibrary> Libraries { get; }

    /// <summary>
    /// Gets the assemblies of the package, by file name (e.g. <c>Kazo.Webamp.dll</c>): the declared
    /// ones and the libraries; satellites are not among them.
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> Assemblies => Files
        .Where(static pair => pair.Key.StartsWith(LibraryFolder, StringComparison.Ordinal) && pair.Key.IndexOf('/', LibraryFolder.Length) < 0)
        .ToDictionary(static pair => pair.Key[LibraryFolder.Length..], static pair => pair.Value, StringComparer.Ordinal);

    /// <summary>
    /// Gets the satellite assemblies of the app's assemblies (ADR 0222 §1), by their path under
    /// <c>lib/</c> (e.g. <c>fr/Kazo.Webamp.resources.dll</c>).
    /// </summary>
    public IReadOnlyDictionary<string, byte[]> SatelliteAssemblies => Files
        .Where(static pair => TryParseSatellitePath(pair.Key, out _, out _))
        .ToDictionary(static pair => pair.Key[LibraryFolder.Length..], static pair => pair.Value, StringComparer.Ordinal);

    /// <summary>
    /// Reads a satellite assembly's archive path, <c>lib/&lt;culture&gt;/&lt;assembly&gt;.resources.dll</c>
    /// (ADR 0222 §1). The culture is a BCP 47 name made of letters, digits and dashes; whether it
    /// belongs to a declared assembly is checked by <see cref="Read"/>.
    /// </summary>
    /// <param name="path">An archive path.</param>
    /// <param name="culture">The culture folder, e.g. <c>fr</c>.</param>
    /// <param name="assemblyFileName">The file name of the assembly it translates, e.g. <c>Kazo.Webamp.dll</c>.</param>
    /// <returns><see langword="true"/> when the path has the satellite form.</returns>
    public static bool TryParseSatellitePath(string path, out string culture, out string assemblyFileName)
    {
        ArgumentNullException.ThrowIfNull(path);
        culture = string.Empty;
        assemblyFileName = string.Empty;
        string[] segments = path.Split('/');
        if (segments.Length != 3
            || segments[0] + "/" != LibraryFolder
            || !IsCultureName(segments[1])
            || !segments[2].EndsWith(SatelliteSuffix, StringComparison.OrdinalIgnoreCase)
            || segments[2].Length == SatelliteSuffix.Length)
        {
            return false;
        }

        culture = segments[1];
        assemblyFileName = segments[2][..^SatelliteSuffix.Length] + ".dll";
        return true;
    }

    private static bool IsCultureName(string name) =>
        name.Length is > 0 and <= MaximumCultureNameLength
        && char.IsAsciiLetter(name[0])
        && name.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-')
        && !name.EndsWith('-')
        && !name.Contains("--", StringComparison.Ordinal);

    /// <summary>
    /// Gets whether a path names a package file: <see cref="Extension"/>, or
    /// <see cref="LegacyExtension"/> for a package published before the rename (ADR 0228 §2).
    /// </summary>
    /// <param name="path">A file name or path, such as <c>1.0.0/org.x.y-1.0.0.kazoapp</c>.</param>
    public static bool HasPackageExtension(string path) =>
        path is not null
        && (path.EndsWith(Extension, StringComparison.Ordinal) || path.EndsWith(LegacyExtension, StringComparison.Ordinal));

    private static bool IsDescriptionFile(string path) => path is DescriptionFileName or LegacyDescriptionFileName;

    /// <summary>Reads and verifies a package.</summary>
    /// <param name="archive">The <c>.kazoapp</c> bytes.</param>
    /// <exception cref="AppPackageException">The package is malformed, incomplete, or altered.</exception>
    public static AppPackage Read(byte[] archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (archive.LongLength > MaximumArchiveBytes)
        {
            throw new AppPackageException("apps.package.too-large", $"The package is larger than {MaximumArchiveBytes / (1024 * 1024)} MiB.");
        }

        Dictionary<string, byte[]> files = Extract(archive);
        // ADR 0228 §2: a package made before the rename names its description hackeros-package.json.
        if (!files.TryGetValue(DescriptionFileName, out byte[]? descriptionBytes)
            && !files.TryGetValue(LegacyDescriptionFileName, out descriptionBytes))
        {
            throw new AppPackageException("apps.package.no-description", $"The package has no {DescriptionFileName}.");
        }

        PackageDescription description = ReadDescription(descriptionBytes);
        foreach (string path in files.Keys.Where(static path => !IsDescriptionFile(path)))
        {
            if (!description.Files.ContainsKey(path))
            {
                throw new AppPackageException("apps.package.unlisted-file", $"'{path}' is in the package but not in {DescriptionFileName}.");
            }
        }

        foreach ((string path, string sha256) in description.Files)
        {
            if (!files.TryGetValue(path, out byte[]? bytes))
            {
                throw new AppPackageException("apps.package.missing-file", $"'{path}' is listed in {DescriptionFileName} but missing.");
            }

            if (!string.Equals(Sha256(bytes), sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new AppPackageException("apps.package.hash-mismatch", $"'{path}' does not match its SHA-256.");
            }
        }

        AppManifest manifest = ReadManifest(files, ManifestFileName);
        if (!string.Equals(manifest.Id, description.AppId, StringComparison.Ordinal))
        {
            throw new AppPackageException("apps.package.id-mismatch", $"The package names '{description.AppId}' but its manifest is '{manifest.Id}'.");
        }

        if (!SemanticVersion.TryParse(manifest.Version, out SemanticVersion manifestVersion) || manifestVersion != description.Version)
        {
            throw new AppPackageException("apps.package.version-mismatch", $"The package is version {description.Version} but its manifest says {manifest.Version}.");
        }

        if (manifest.Parent is not null)
        {
            throw new AppPackageException("apps.package.bad-manifest", $"{ManifestFileName} is the child of '{manifest.Parent}': a child is installed with its feature's package, never alone (ADR 0243).");
        }

        IReadOnlyList<AppManifest> children = ReadChildren(manifest, description, files);
        CheckLibraries(description.Libraries, GetDeclaredAssemblies(children.Prepend(manifest)), files);
        CheckContents(manifest, children, description.Libraries, files);
        return new AppPackage(manifest, children, description.Version, files, description.Libraries);
    }

    /// <summary>
    /// ADR 0244 §1: every listed library is a file of <c>lib/</c> that is the assembly the list says
    /// (same simple name, version, SHA-256 and shared mark), and is not one the manifest declares.
    /// </summary>
    private static void CheckLibraries(IReadOnlyList<AppPackageLibrary> libraries, IReadOnlySet<string> declared, Dictionary<string, byte[]> files)
    {
        foreach (AppPackageLibrary library in libraries)
        {
            if (declared.Contains(library.FileName, StringComparer.OrdinalIgnoreCase))
            {
                throw new AppPackageException("apps.package.bad-description", $"{DescriptionFileName} lists '{library.Name}' as a library, but the manifest declares {library.FileName} as an assembly of the app.");
            }

            if (!files.TryGetValue(LibraryFolder + library.FileName, out byte[]? bytes))
            {
                throw new AppPackageException("apps.package.missing-assembly", $"{DescriptionFileName} lists the library lib/{library.FileName}, which the package does not hold.");
            }

            AppPackageLibrary actual = AppPackageLibrary.Describe(library.FileName, bytes);
            if (!string.Equals(actual.Sha256, library.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new AppPackageException("apps.package.hash-mismatch", $"The library lib/{library.FileName} does not match the SHA-256 of its entry in libraries.");
            }

            if (!string.Equals(actual.Name, library.Name, StringComparison.Ordinal) || actual.Version != library.Version || actual.Shared != library.Shared)
            {
                throw new AppPackageException(
                    "apps.package.bad-description",
                    $"{DescriptionFileName} lists the library {library.Name} {library.Version} (shared: {library.Shared}), but lib/{library.FileName} is {actual.Name} {actual.Version} (shared: {actual.Shared}).");
            }
        }
    }

    /// <summary>
    /// ADR 0243 §2: a feature package carries one manifest per child under <c>entrypoints/</c>,
    /// listed in <c>manifests</c> of the description (every <c>entrypoints/*.json</c> file when the
    /// description predates the list). Any other package carries none.
    /// </summary>
    private static IReadOnlyList<AppManifest> ReadChildren(AppManifest manifest, PackageDescription description, Dictionary<string, byte[]> files)
    {
        string[] listed = description.Manifests.Count > 0
            ? [.. description.Manifests]
            : [.. files.Keys.Where(static path => path.StartsWith(EntryPointsFolder, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
        if (manifest.Kind != AppKind.Feature)
        {
            if (listed.Length > 0)
            {
                throw new AppPackageException("apps.package.unexpected-file", $"'{listed[0]}' is a child manifest, but {ManifestFileName} is not a feature (ADR 0243).");
            }

            return [];
        }

        List<AppManifest> children = [];
        foreach (string path in listed)
        {
            if (!path.StartsWith(EntryPointsFolder, StringComparison.Ordinal) || !path.EndsWith(".json", StringComparison.Ordinal) || path.IndexOf('/', EntryPointsFolder.Length) >= 0)
            {
                throw new AppPackageException("apps.package.bad-description", $"{DescriptionFileName} lists the child manifest '{path}' outside {EntryPointsFolder}<suffix>.json.");
            }

            AppManifest child = ReadManifest(files, path);
            ManifestValidationResult validation = AppManifestValidator.Validate(child);
            if (!validation.IsValid)
            {
                throw new AppPackageException("apps.package.bad-manifest", $"{path} is not valid: {string.Join("; ", validation.Errors.Select(static error => error.Message))}");
            }

            children.Add(child);
        }

        ManifestValidationResult relation = AppManifestValidator.ValidateFeature(manifest, children);
        if (!relation.IsValid)
        {
            throw new AppPackageException("apps.package.bad-manifest", $"The children of {manifest.Id} are not valid: {string.Join("; ", relation.Errors.Select(static error => error.Message))}");
        }

        return children;
    }

    /// <summary>Gets the lower-case hexadecimal SHA-256 of <paramref name="bytes"/>, the form packages and manifests use.</summary>
    public static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Gets whether <paramref name="path"/> is a safe relative path inside a package.</summary>
    public static bool IsSafeRelativePath(string path) =>
        path.Length is > 0 and <= 260
        && !path.StartsWith('/')
        && !path.Contains('\\', StringComparison.Ordinal)
        && !path.Contains(':', StringComparison.Ordinal)
        && path.Split('/').All(static segment => segment is not ("" or "." or "..") && !segment.Any(char.IsControl));

    private static Dictionary<string, byte[]> Extract(byte[] archive) =>
        Extract(archive, MaximumExtractedBytes, MaximumFiles, "package");

    /// <summary>
    /// Unzips an archive into memory with the checks every Kazo OS archive gets: safe relative
    /// paths only, at most <paramref name="maximumFiles"/> files and <paramref name="maximumBytes"/>
    /// bytes once expanded, consistent sizes, no duplicate. Shared by <c>.kazoapp</c> and
    /// <c>.kazotheme</c> (ADR 0213 §1).
    /// </summary>
    /// <param name="archive">The zip bytes.</param>
    /// <param name="maximumBytes">Largest total of the expanded files.</param>
    /// <param name="maximumFiles">Most files accepted.</param>
    /// <param name="kind">What the archive is, for the messages (<c>package</c>, <c>theme pack</c>).</param>
    /// <exception cref="AppPackageException">A check failed; the code says which.</exception>
    internal static Dictionary<string, byte[]> Extract(byte[] archive, long maximumBytes, int maximumFiles, string kind)
    {
        Dictionary<string, byte[]> files = new(StringComparer.Ordinal);
        long total = 0;
        try
        {
            using ZipArchive zip = new(new MemoryStream(archive, writable: false), ZipArchiveMode.Read);
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/'))
                {
                    continue;
                }

                if (!IsSafeRelativePath(entry.FullName))
                {
                    throw new AppPackageException("apps.package.unsafe-path", $"'{entry.FullName}' is not a safe path.");
                }

                if (files.Count >= maximumFiles)
                {
                    throw new AppPackageException("apps.package.too-many-files", $"The {kind} holds more than {maximumFiles} files.");
                }

                total += entry.Length;
                if (entry.Length < 0 || total > maximumBytes)
                {
                    throw new AppPackageException("apps.package.too-large", $"The {kind} expands to more than {maximumBytes / (1024 * 1024)} MiB.");
                }

                using Stream stream = entry.Open();
                using MemoryStream buffer = new();
                stream.CopyTo(buffer);
                if (buffer.Length != entry.Length)
                {
                    throw new AppPackageException("apps.package.corrupt", $"'{entry.FullName}' has an inconsistent size.");
                }

                if (!files.TryAdd(entry.FullName, buffer.ToArray()))
                {
                    throw new AppPackageException("apps.package.duplicate-file", $"'{entry.FullName}' appears twice.");
                }
            }
        }
        catch (InvalidDataException exception)
        {
            throw new AppPackageException("apps.package.corrupt", $"The {kind} is not a valid zip archive: {exception.Message}");
        }

        return files;
    }

    private static PackageDescription ReadDescription(byte[] bytes)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            string appId = root.GetProperty("appId").GetString() ?? string.Empty;
            string versionText = root.GetProperty("version").GetString() ?? string.Empty;
            if (appId.Length == 0 || !SemanticVersion.TryParse(versionText, out SemanticVersion version))
            {
                throw new AppPackageException("apps.package.bad-description", $"{DescriptionFileName} needs an appId and a version.");
            }

            Dictionary<string, string> listed = new(StringComparer.Ordinal);
            foreach (JsonProperty file in root.GetProperty("files").EnumerateObject())
            {
                string hash = file.Value.GetString() ?? string.Empty;
                if (!IsSafeRelativePath(file.Name) || IsDescriptionFile(file.Name) || hash.Length != 64 || !hash.All(Uri.IsHexDigit))
                {
                    throw new AppPackageException("apps.package.bad-description", $"{DescriptionFileName} lists '{file.Name}' with an invalid path or SHA-256.");
                }

                listed[file.Name] = hash;
            }

            // ADR 0243 §2: the child manifests of a feature, in the order the feature declares them.
            List<string> manifests = [];
            if (root.TryGetProperty("manifests", out JsonElement manifestList) && manifestList.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in manifestList.EnumerateArray())
                {
                    string path = item.GetString() ?? string.Empty;
                    if (!listed.ContainsKey(path))
                    {
                        throw new AppPackageException("apps.package.bad-description", $"{DescriptionFileName} lists the manifest '{path}', which is not among its files.");
                    }

                    manifests.Add(path);
                }
            }

            return new PackageDescription(appId, version, listed, manifests, AppPackageLibrary.ReadList(root));
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new AppPackageException("apps.package.bad-description", $"{DescriptionFileName} is not valid: {exception.Message}");
        }
    }

    private static AppManifest ReadManifest(Dictionary<string, byte[]> files, string path)
    {
        if (!files.TryGetValue(path, out byte[]? bytes))
        {
            throw new AppPackageException("apps.package.no-manifest", $"The package has no {path}.");
        }

        try
        {
            return AppManifestJsonSerializer.NormalizeOptionalCollections(
                AppManifestJsonSerializer.DeserializeStrict(System.Text.Encoding.UTF8.GetString(bytes)));
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            throw new AppPackageException("apps.package.bad-manifest", $"{path} is not a valid app manifest: {exception.Message}");
        }
    }

    private static void CheckContents(AppManifest manifest, IReadOnlyList<AppManifest> children, IReadOnlyList<AppPackageLibrary> libraries, Dictionary<string, byte[]> files)
    {
        ManifestValidationResult validation = AppManifestValidator.Validate(manifest);
        if (!validation.IsValid)
        {
            throw new AppPackageException("apps.package.bad-manifest", $"{ManifestFileName} is not valid: {string.Join("; ", validation.Errors.Select(static error => error.Message))}");
        }

        IReadOnlySet<string> declared = GetDeclaredAssemblies(children.Prepend(manifest));
        // ADR 0244 §1: the libraries of the app have their satellites too.
        HashSet<string> translatable = new(declared.Concat(libraries.Select(static library => library.FileName)), StringComparer.Ordinal);
        bool feature = manifest.Kind == AppKind.Feature;
        foreach (string path in files.Keys)
        {
            // ADR 0222 §1: a satellite translates one of the app's own assemblies, never the SDK's
            // (the OS brings those) nor another package's.
            bool known = path is DescriptionFileName or LegacyDescriptionFileName or ManifestFileName
                || (path.StartsWith(LibraryFolder, StringComparison.Ordinal) && path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && path.Count(static c => c == '/') == 1)
                || (TryParseSatellitePath(path, out _, out string translated) && translatable.Contains(translated))
                || path.StartsWith(AssetFolder, StringComparison.Ordinal)
                // ADR 0243 §2: the children of a feature, already read and validated.
                || (feature && path.StartsWith(EntryPointsFolder, StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal) && path.Count(static c => c == '/') == 1);
            if (!known)
            {
                throw new AppPackageException("apps.package.unexpected-file", $"'{path}' is outside manifest.json, lib/*.dll, lib/<culture>/<declared assembly>.resources.dll, wwwroot/ and, for a feature, entrypoints/*.json.");
            }
        }

        foreach (string assembly in declared)
        {
            if (!files.ContainsKey(LibraryFolder + assembly))
            {
                throw new AppPackageException("apps.package.missing-assembly", $"The manifest needs lib/{assembly}, which the package does not hold.");
            }
        }

        foreach (AssetManifest asset in children.Prepend(manifest).SelectMany(static item => item.Assets).DistinctBy(static asset => asset.Path, StringComparer.Ordinal))
        {
            if (!files.TryGetValue(AssetFolder + asset.Path, out byte[]? bytes))
            {
                throw new AppPackageException("apps.package.missing-asset", $"The manifest declares the asset '{asset.Path}', which the package does not hold.");
            }

            if (!string.Equals(Sha256(bytes), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new AppPackageException("apps.package.hash-mismatch", $"The asset '{asset.Path}' does not match the SHA-256 of its manifest.");
            }
        }
    }

    /// <summary>Gets every assembly a manifest names: entry points of all platforms and components.</summary>
    /// <param name="manifest">A valid manifest.</param>
    public static IReadOnlySet<string> GetDeclaredAssemblies(AppManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return GetDeclaredAssemblies([manifest]);
    }

    /// <summary>Gets every assembly a set of manifests names (a feature and its children, ADR 0243 §2).</summary>
    /// <param name="manifests">Valid manifests.</param>
    public static IReadOnlySet<string> GetDeclaredAssemblies(IEnumerable<AppManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        HashSet<string> assemblies = new(StringComparer.Ordinal);
        foreach (AppManifest manifest in manifests)
        {
            if (AppManifestPlatformSupport.Resolve(manifest) is { } platforms)
            {
                assemblies.UnionWith(platforms.EntryPointsByPlatform.Values.Select(static entryPoint => entryPoint.Assembly));
            }

            assemblies.UnionWith((manifest.Components ?? []).Select(static component => component.Assembly));
        }

        return assemblies;
    }

    private sealed record PackageDescription(
        string AppId,
        SemanticVersion Version,
        IReadOnlyDictionary<string, string> Files,
        IReadOnlyList<string> Manifests,
        IReadOnlyList<AppPackageLibrary> Libraries);
}
