using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kazo.Os.Platform.Core.Apps.Packages;

namespace Kazo.Os.Tools.ManifestGenerator;

/// <summary>
/// The API surface of one published Kazo OS version (ADR 0277 §1): every assembly the trimmed
/// publish ships, with the types it defines or forwards and, for each type, the methods, constructors
/// and fields reflection finds on it. It answers the same questions as
/// <see cref="LoadedRuntimeApiSurface"/> does inside the OS, so <see cref="AppPackageApiCheck"/> gives
/// the same verdict at build time as at install time.
/// </summary>
/// <remarks>
/// Members are stored already flattened the way <see cref="LoadedRuntimeApiSurface"/> looks them up
/// (inherited members, and for an interface its base interfaces and <see cref="object"/>), so a
/// lookup is a set test. The file is JSON compressed with gzip; <see cref="Read(Stream)"/> also
/// accepts plain JSON.
/// </remarks>
internal sealed class ApiSurfaceFile : IRuntimeApiSurface
{
    /// <summary>The current format of the file.</summary>
    public const int CurrentFormat = 1;

    /// <summary>Bounds the forwarder chain a lookup follows, so a cyclic file cannot loop.</summary>
    private const int MaxForwardHops = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    private readonly Dictionary<string, AssemblyIndex> _assemblies;

    /// <summary>Creates a surface from its document.</summary>
    /// <param name="document">The parsed document.</param>
    /// <exception cref="InvalidDataException">The document has an unsupported format.</exception>
    public ApiSurfaceFile(ApiSurfaceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Format != CurrentFormat)
        {
            throw new InvalidDataException($"API surface format {document.Format} is not supported (expected {CurrentFormat}).");
        }

        Document = document;
        _assemblies = new Dictionary<string, AssemblyIndex>(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, ApiSurfaceAssembly assembly) in document.Assemblies ?? [])
        {
            _assemblies[name] = new AssemblyIndex(assembly);
        }
    }

    /// <summary>Gets the document this surface reads.</summary>
    public ApiSurfaceDocument Document { get; }

    /// <summary>Gets the OS version the surface describes, e.g. <c>3.4.1</c>.</summary>
    public string OsVersion => Document.OsVersion;

    /// <summary>Reads a surface from a gzip-compressed or plain JSON stream.</summary>
    /// <param name="stream">The stream, read from its current position.</param>
    /// <returns>The surface.</returns>
    /// <exception cref="InvalidDataException">The content is not a surface of a supported format.</exception>
    public static ApiSurfaceFile Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        byte[] bytes = buffer.ToArray();
        bool gzip = bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b;
        ApiSurfaceDocument? document;
        try
        {
            using Stream json = gzip ? new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress) : new MemoryStream(bytes);
            document = JsonSerializer.Deserialize<ApiSurfaceDocument>(json, JsonOptions);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            throw new InvalidDataException("The API surface file is not valid gzip-compressed JSON.", exception);
        }

        return new ApiSurfaceFile(document ?? throw new InvalidDataException("The API surface file is empty."));
    }

    /// <summary>Reads a surface from a file.</summary>
    /// <param name="path">Path of a <c>.json</c> or <c>.json.gz</c> file.</param>
    /// <returns>The surface.</returns>
    public static ApiSurfaceFile Read(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>Writes the surface as gzip-compressed JSON.</summary>
    /// <param name="stream">The destination.</param>
    public void Write(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using GZipStream gzip = new(stream, CompressionLevel.SmallestSize, leaveOpen: true);
        JsonSerializer.Serialize(gzip, Document, JsonOptions);
    }

    /// <summary>Writes the surface to a file as gzip-compressed JSON, creating its folder.</summary>
    /// <param name="path">The destination path.</param>
    public void Write(string path)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is not null)
        {
            Directory.CreateDirectory(folder);
        }

        using FileStream stream = File.Create(path);
        Write(stream);
    }

    /// <summary>Gets the key of a method in <see cref="ApiSurfaceType.Methods"/>: <c>Name`arity(type,type)</c>.</summary>
    /// <param name="methodName">Method name, <c>.ctor</c> or <c>.cctor</c> for constructors.</param>
    /// <param name="genericArity">Generic parameter count of the method.</param>
    /// <param name="parameterTypes">Parameter types in the text form of <see cref="RuntimeTypeNames"/>.</param>
    /// <returns>The key.</returns>
    public static string MethodKey(string methodName, int genericArity, IEnumerable<string> parameterTypes) =>
        $"{methodName}`{genericArity}{RuntimeTypeNames.Parameters(parameterTypes)}";

    /// <inheritdoc />
    public bool HasAssembly(string assemblyName) => _assemblies.ContainsKey(assemblyName);

    /// <inheritdoc />
    public bool HasType(string assemblyName, string typeName) => Find(assemblyName, typeName) is not null;

    /// <inheritdoc />
    public bool HasMethod(string assemblyName, string typeName, string methodName, int genericArity, int parameterCount)
    {
        TypeIndex? type = Find(assemblyName, typeName);
        if (type is null)
        {
            return false;
        }

        if (methodName == ".ctor" && type.IsValueType && parameterCount == 0)
        {
            return true;
        }

        string prefix = $"{methodName}`{genericArity}(";
        return type.Methods.Any(key => key.StartsWith(prefix, StringComparison.Ordinal) && CountParameters(key, prefix.Length) == parameterCount);
    }

    /// <inheritdoc />
    public bool HasMethod(string assemblyName, string typeName, string methodName, int genericArity, IReadOnlyList<string> parameterTypes)
    {
        ArgumentNullException.ThrowIfNull(parameterTypes);
        TypeIndex? type = Find(assemblyName, typeName);
        if (type is null)
        {
            return false;
        }

        // A struct always has its parameterless constructor (LoadedRuntimeApiSurface does the same).
        if (methodName == ".ctor" && type.IsValueType && parameterTypes.Count == 0)
        {
            return true;
        }

        return type.Methods.Contains(MethodKey(methodName, genericArity, parameterTypes));
    }

    /// <inheritdoc />
    public bool HasField(string assemblyName, string typeName, string fieldName) =>
        Find(assemblyName, typeName)?.Fields.Contains(fieldName) == true;

    /// <summary>Finds a type, following the forwarders of the assembly it was asked in.</summary>
    private TypeIndex? Find(string assemblyName, string typeName)
    {
        string current = assemblyName;
        for (int hop = 0; hop <= MaxForwardHops; hop++)
        {
            if (!_assemblies.TryGetValue(current, out AssemblyIndex? assembly))
            {
                return null;
            }

            if (assembly.Types.TryGetValue(typeName, out TypeIndex? type))
            {
                return type;
            }

            if (assembly.Forwards is null || !assembly.Forwards.TryGetValue(typeName, out string? target))
            {
                return null;
            }

            current = target;
        }

        return null;
    }

    /// <summary>Counts the top-level parameters of a key, skipping the commas inside generic arguments.</summary>
    private static int CountParameters(string key, int start)
    {
        int end = key.Length - 1;
        if (end <= start)
        {
            return 0;
        }

        int count = 1;
        int depth = 0;
        for (int index = start; index < end; index++)
        {
            switch (key[index])
            {
                case '<':
                case '[':
                    depth++;
                    break;
                case '>':
                case ']':
                    depth--;
                    break;
                case ',' when depth == 0:
                    count++;
                    break;
            }
        }

        return count;
    }

    private sealed class AssemblyIndex
    {
        public AssemblyIndex(ApiSurfaceAssembly assembly)
        {
            Types = (assembly.Types ?? []).ToDictionary(
                static pair => pair.Key,
                static pair => new TypeIndex(pair.Value),
                StringComparer.Ordinal);
            Forwards = assembly.Forwards is { Count: > 0 } forwards
                ? new Dictionary<string, string>(forwards, StringComparer.Ordinal)
                : null;
        }

        public Dictionary<string, TypeIndex> Types { get; }

        public Dictionary<string, string>? Forwards { get; }
    }

    private sealed class TypeIndex(ApiSurfaceType type)
    {
        public bool IsValueType { get; } = type.ValueType;

        public HashSet<string> Methods { get; } = new(type.Methods ?? [], StringComparer.Ordinal);

        public HashSet<string> Fields { get; } = new(type.Fields ?? [], StringComparer.Ordinal);
    }
}

/// <summary>The JSON document of an <see cref="ApiSurfaceFile"/>.</summary>
/// <param name="Format">Format version, <see cref="ApiSurfaceFile.CurrentFormat"/>.</param>
/// <param name="OsVersion">The Kazo OS version the surface describes.</param>
/// <param name="Assemblies">Every published assembly, by name.</param>
internal sealed record ApiSurfaceDocument(
    [property: JsonPropertyName("format")] int Format,
    [property: JsonPropertyName("osVersion")] string OsVersion,
    [property: JsonPropertyName("assemblies")] SortedDictionary<string, ApiSurfaceAssembly> Assemblies);

/// <summary>One assembly of an <see cref="ApiSurfaceDocument"/>.</summary>
/// <param name="Types">The types it defines, by full name (nested types use <c>+</c>).</param>
/// <param name="Forwards">The types it forwards, by full name, to the assembly that defines them.</param>
internal sealed record ApiSurfaceAssembly(
    [property: JsonPropertyName("types")] SortedDictionary<string, ApiSurfaceType> Types,
    [property: JsonPropertyName("forwards")] SortedDictionary<string, string>? Forwards = null);

/// <summary>One type of an <see cref="ApiSurfaceAssembly"/>.</summary>
/// <param name="Methods">Method and constructor keys (<see cref="ApiSurfaceFile.MethodKey"/>), inherited ones included.</param>
/// <param name="Fields">Field names, inherited ones included.</param>
/// <param name="ValueType">Whether the type is a struct, which always has a parameterless constructor.</param>
internal sealed record ApiSurfaceType(
    [property: JsonPropertyName("m")] string[]? Methods = null,
    [property: JsonPropertyName("f")] string[]? Fields = null,
    [property: JsonPropertyName("v")] bool ValueType = false);
