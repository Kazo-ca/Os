using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace Kazo.Os.Platform.Core.Apps.Packages;

/// <summary>
/// One library of an app, carried in its package under <c>lib/</c> and listed in <c>libraries</c> of
/// <c>kazoos-package.json</c> (ADR 0244 §1): an assembly the app needs that neither the OS nor the
/// .NET runtime brings, such as the output of a <c>ProjectReference</c> or a NuGet package.
/// </summary>
/// <param name="Name">Simple assembly name, e.g. <c>Kazo.Irc.Protocol</c>; its file is <c>lib/&lt;name&gt;.dll</c>.</param>
/// <param name="Version">Assembly version; its major part is the compatibility of a shared library (ADR 0244 §2).</param>
/// <param name="Sha256">Lower-case hexadecimal SHA-256 of the file.</param>
/// <param name="Shared">
/// Whether the author of the library marked it as a shared contract (ADR 0244 §2): several apps carry
/// it and the OS loads one copy per page. The mark is in the assembly itself
/// (<see cref="SharedMetadataKey"/>); an app cannot set it for a library that is not marked.
/// </param>
public sealed record AppPackageLibrary(string Name, Version Version, string Sha256, bool Shared)
{
    /// <summary>
    /// Key of the <c>AssemblyMetadata</c> attribute that marks a shared library, with the value
    /// <c>true</c>. The MSBuild property <c>KazoOsSharedLibrary</c> of the library project writes it.
    /// </summary>
    public const string SharedMetadataKey = "Kazo.Os.SharedLibrary";

    /// <summary>Name of the list in <c>kazoos-package.json</c>.</summary>
    public const string ListName = "libraries";

    /// <summary>Longest simple name accepted for a library.</summary>
    private const int MaximumNameLength = 200;

    /// <summary>Gets the file name under <c>lib/</c>, e.g. <c>Kazo.Irc.Protocol.dll</c>.</summary>
    public string FileName => Name + ".dll";

    /// <summary>Reads what an assembly says of itself: simple name, version and the shared mark.</summary>
    /// <param name="fileName">Its file name, for the messages.</param>
    /// <param name="bytes">The assembly.</param>
    /// <exception cref="AppPackageException"><c>apps.package.bad-assembly</c>: not a .NET assembly.</exception>
    public static AppPackageLibrary Describe(string fileName, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            using PEReader pe = new(ImmutableArray.Create(bytes));
            if (!pe.HasMetadata)
            {
                throw new AppPackageException("apps.package.bad-assembly", $"lib/{fileName} is not a .NET assembly.");
            }

            MetadataReader reader = pe.GetMetadataReader();
            if (!reader.IsAssembly)
            {
                throw new AppPackageException("apps.package.bad-assembly", $"lib/{fileName} is a module, not an assembly.");
            }

            AssemblyDefinition assembly = reader.GetAssemblyDefinition();
            return new AppPackageLibrary(
                reader.GetString(assembly.Name),
                assembly.Version,
                AppPackage.Sha256(bytes),
                IsMarkedShared(reader, assembly));
        }
        catch (BadImageFormatException exception)
        {
            throw new AppPackageException("apps.package.bad-assembly", $"lib/{fileName} is not a valid .NET assembly: {exception.Message}");
        }
    }

    /// <summary>Looks for <c>[assembly: AssemblyMetadata("Kazo.Os.SharedLibrary", "true")]</c>.</summary>
    private static bool IsMarkedShared(MetadataReader reader, AssemblyDefinition assembly)
    {
        foreach (CustomAttributeHandle handle in assembly.GetCustomAttributes())
        {
            CustomAttribute attribute = reader.GetCustomAttribute(handle);
            if (!IsAssemblyMetadataAttribute(reader, attribute))
            {
                continue;
            }

            // Two string arguments after the two-byte prolog (0x0001): the key, then the value.
            BlobReader blob = reader.GetBlobReader(attribute.Value);
            if (blob.Length < 4 || blob.ReadUInt16() != 1)
            {
                continue;
            }

            string? key = blob.ReadSerializedString();
            string? value = blob.ReadSerializedString();
            if (string.Equals(key, SharedMetadataKey, StringComparison.Ordinal))
            {
                return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private static bool IsAssemblyMetadataAttribute(MetadataReader reader, CustomAttribute attribute)
    {
        EntityHandle type;
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                type = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                break;
            case HandleKind.MethodDefinition:
                type = reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
                break;
            default:
                return false;
        }

        StringHandle name;
        StringHandle ns;
        switch (type.Kind)
        {
            case HandleKind.TypeReference:
                TypeReference reference = reader.GetTypeReference((TypeReferenceHandle)type);
                (name, ns) = (reference.Name, reference.Namespace);
                break;
            case HandleKind.TypeDefinition:
                TypeDefinition definition = reader.GetTypeDefinition((TypeDefinitionHandle)type);
                (name, ns) = (definition.Name, definition.Namespace);
                break;
            default:
                return false;
        }

        return reader.StringComparer.Equals(name, "AssemblyMetadataAttribute")
            && reader.StringComparer.Equals(ns, "System.Reflection");
    }

    /// <summary>
    /// Reads <c>libraries</c> of a package description (ADR 0244 §1). A description written before
    /// the list existed has none.
    /// </summary>
    /// <param name="root">The root object of <c>kazoos-package.json</c>.</param>
    /// <returns>The libraries, in the order listed.</returns>
    /// <exception cref="AppPackageException"><c>apps.package.bad-description</c>.</exception>
    public static IReadOnlyList<AppPackageLibrary> ReadList(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty(ListName, out JsonElement list)
            || list.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (list.ValueKind != JsonValueKind.Array)
        {
            throw Bad("libraries is not a list.");
        }

        List<AppPackageLibrary> libraries = [];
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in list.EnumerateArray())
        {
            AppPackageLibrary library = ReadItem(item);
            if (!names.Add(library.Name))
            {
                throw Bad($"the library {library.Name} is listed twice.");
            }

            libraries.Add(library);
        }

        return libraries;
    }

    private static AppPackageLibrary ReadItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("name", out JsonElement nameElement) || nameElement.ValueKind != JsonValueKind.String
            || !item.TryGetProperty("version", out JsonElement versionElement) || versionElement.ValueKind != JsonValueKind.String
            || !item.TryGetProperty("sha256", out JsonElement hashElement) || hashElement.ValueKind != JsonValueKind.String)
        {
            throw Bad("a library needs a name, a version and a sha256.");
        }

        string name = nameElement.GetString()!;
        string hash = hashElement.GetString()!;
        if (!IsLibraryName(name))
        {
            throw Bad($"\"{name}\" is not an assembly name.");
        }

        if (!Version.TryParse(versionElement.GetString(), out Version? version))
        {
            throw Bad($"the library {name} has the invalid version \"{versionElement.GetString()}\".");
        }

        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit))
        {
            throw Bad($"the library {name} has an invalid SHA-256.");
        }

        bool shared = item.TryGetProperty("shared", out JsonElement sharedElement) && sharedElement.ValueKind == JsonValueKind.True;
        return new AppPackageLibrary(name, version, hash.ToLowerInvariant(), shared);
    }

    /// <summary>Writes <c>libraries</c> into a package description being written.</summary>
    /// <param name="writer">The writer, inside the root object.</param>
    /// <param name="libraries">The libraries; nothing is written for none.</param>
    internal static void WriteList(Utf8JsonWriter writer, IReadOnlyList<AppPackageLibrary> libraries)
    {
        if (libraries.Count == 0)
        {
            return;
        }

        writer.WriteStartArray(ListName);
        foreach (AppPackageLibrary library in libraries)
        {
            writer.WriteStartObject();
            writer.WriteString("name", library.Name);
            writer.WriteString("version", library.Version.ToString());
            writer.WriteString("sha256", library.Sha256);
            writer.WriteBoolean("shared", library.Shared);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    /// <summary>Gets whether a text can be the simple name of an assembly held as <c>lib/&lt;name&gt;.dll</c>.</summary>
    private static bool IsLibraryName(string name) =>
        name.Length is > 0 and <= MaximumNameLength
        && name.All(IsNameCharacter)
        && !name.StartsWith('.')
        && !name.EndsWith('.')
        && !name.EndsWith(".resources", StringComparison.OrdinalIgnoreCase);

    private static bool IsNameCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_';

    private static AppPackageException Bad(string message) =>
        new("apps.package.bad-description", $"{AppPackage.DescriptionFileName} is not valid: {message}");
}
