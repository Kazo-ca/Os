using System.Reflection;
using Kazo.Os.Platform.Core.Apps.Packages;

namespace Kazo.Os.Tools.ManifestGenerator;

/// <summary>
/// Builds the <see cref="ApiSurfaceFile"/> of a published OS (ADR 0277 §1) from the assemblies ILLink
/// wrote (<c>obj/Release/net10.0/linked/</c>), before Webcil wraps them in <c>.wasm</c> files.
/// </summary>
/// <remarks>
/// The assemblies are read in a <see cref="MetadataLoadContext"/>, so nothing is loaded into this
/// process, and every lookup copies <see cref="LoadedRuntimeApiSurface"/>: the same binding flags, the
/// same flattening and the same parameter type names (<see cref="RuntimeTypeNames.Of"/>). A member
/// whose signature names an assembly the publish does not ship is left out: the OS could not bind it
/// either.
/// </remarks>
internal static class ApiSurfaceGenerator
{
    private const BindingFlags AllMembers = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;

    private const string CoreAssemblyName = "System.Private.CoreLib";

    /// <summary>Reads the surface of every assembly in <paramref name="linkedDirectory"/>.</summary>
    /// <param name="linkedDirectory">The folder of the trimmed assemblies (<c>*.dll</c>).</param>
    /// <param name="osVersion">The OS version the publish is, written in the file.</param>
    /// <returns>The surface.</returns>
    /// <exception cref="DirectoryNotFoundException">The folder does not exist.</exception>
    /// <exception cref="InvalidDataException">The folder has no <c>System.Private.CoreLib.dll</c>.</exception>
    public static ApiSurfaceFile Generate(string linkedDirectory, string osVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkedDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(osVersion);
        if (!Directory.Exists(linkedDirectory))
        {
            throw new DirectoryNotFoundException($"The trimmed assemblies folder '{linkedDirectory}' does not exist.");
        }

        string[] paths = [.. Directory.EnumerateFiles(linkedDirectory, "*.dll").Order(StringComparer.Ordinal)];
        if (!paths.Any(path => Path.GetFileNameWithoutExtension(path).Equals(CoreAssemblyName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException($"'{linkedDirectory}' has no {CoreAssemblyName}.dll: it is not the output of a trimmed publish.");
        }

        using MetadataLoadContext context = new(new PathAssemblyResolver(paths), CoreAssemblyName);
        Type objectType = context.CoreAssembly!.GetType("System.Object", throwOnError: true)!;
        SortedDictionary<string, ApiSurfaceAssembly> assemblies = new(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            Assembly assembly;
            try
            {
                assembly = context.LoadFromAssemblyPath(path);
            }
            catch (BadImageFormatException)
            {
                // Not a managed assembly (a native helper copied next to the others).
                continue;
            }

            assemblies[assembly.GetName().Name!] = ReadAssembly(assembly, objectType);
        }

        return new ApiSurfaceFile(new ApiSurfaceDocument(ApiSurfaceFile.CurrentFormat, osVersion, assemblies));
    }

    private static ApiSurfaceAssembly ReadAssembly(Assembly assembly, Type objectType)
    {
        SortedDictionary<string, ApiSurfaceType> types = new(StringComparer.Ordinal);
        foreach (Type type in LoadableTypes(assembly))
        {
            if (type.FullName is { } name)
            {
                types[name] = ReadType(type, objectType);
            }
        }

        SortedDictionary<string, string> forwards = new(StringComparer.Ordinal);
        foreach (Type forwarded in ForwardedTypes(assembly))
        {
            if (forwarded.FullName is { } name && forwarded.Assembly.GetName().Name is { } target)
            {
                forwards[name] = target;
            }
        }

        return new ApiSurfaceAssembly(types, forwards.Count > 0 ? forwards : null);
    }

    private static ApiSurfaceType ReadType(Type type, Type objectType)
    {
        SortedSet<string> methods = new(StringComparer.Ordinal);
        SortedSet<string> fields = new(StringComparer.Ordinal);

        // Same lookups as LoadedRuntimeApiSurface: declared constructors, flattened methods and fields,
        // and for an interface its base interfaces and System.Object.
        AddMembers(methods, () => type.GetConstructors(AllMembers & ~BindingFlags.FlattenHierarchy), static constructor =>
            ApiSurfaceFile.MethodKey(constructor.IsStatic ? ".cctor" : ".ctor", 0, ParameterTypes(constructor)));
        foreach (Type candidate in CandidateTypes(type, objectType))
        {
            AddMembers(methods, () => candidate.GetMethods(AllMembers), static method =>
                ApiSurfaceFile.MethodKey(method.Name, method.IsGenericMethodDefinition ? method.GetGenericArguments().Length : 0, ParameterTypes(method)));
            AddMembers(fields, () => candidate.GetFields(AllMembers), static field => field.Name);
        }

        return new ApiSurfaceType(
            methods.Count > 0 ? [.. methods] : null,
            fields.Count > 0 ? [.. fields] : null,
            type.IsValueType);
    }

    /// <summary>Adds the key of each member, leaving out a member whose signature cannot be resolved.</summary>
    private static void AddMembers<TMember>(SortedSet<string> keys, Func<TMember[]> members, Func<TMember, string> key)
    {
        TMember[] found;
        try
        {
            found = members();
        }
        catch (Exception exception) when (IsUnresolvable(exception))
        {
            return;
        }

        foreach (TMember member in found)
        {
            try
            {
                keys.Add(key(member));
            }
            catch (Exception exception) when (IsUnresolvable(exception))
            {
                // A parameter type lives in an assembly the publish dropped: the OS cannot bind it either.
            }
        }
    }

    private static IEnumerable<Type> CandidateTypes(Type type, Type objectType)
    {
        if (!type.IsInterface)
        {
            return [type];
        }

        Type[] interfaces;
        try
        {
            interfaces = type.GetInterfaces();
        }
        catch (Exception exception) when (IsUnresolvable(exception))
        {
            interfaces = [];
        }

        return [type, .. interfaces, objectType];
    }

    private static IEnumerable<string> ParameterTypes(MethodBase method) =>
        method.GetParameters().Select(static parameter => RuntimeTypeNames.Of(parameter.ParameterType));

    private static Type[] LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return [.. exception.Types.OfType<Type>()];
        }
    }

    private static Type[] ForwardedTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetForwardedTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            return [.. exception.Types.OfType<Type>()];
        }
    }

    private static bool IsUnresolvable(Exception exception) =>
        exception is FileNotFoundException or FileLoadException or TypeLoadException or BadImageFormatException or NotSupportedException;
}
