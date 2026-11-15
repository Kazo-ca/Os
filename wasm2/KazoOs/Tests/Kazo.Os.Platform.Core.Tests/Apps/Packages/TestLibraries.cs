using System.Reflection;
using System.Reflection.Emit;
using Kazo.Os.Platform.Core.Apps.Packages;

namespace Kazo.Os.Platform.Core.Tests.Apps.Packages;

/// <summary>
/// Makes small real assemblies for the library tests of ADR 0261: a package library is checked
/// against what its assembly says of itself (simple name, version, shared mark).
/// </summary>
internal static class TestLibraries
{
    /// <summary>Builds an assembly with one public type.</summary>
    /// <param name="name">Simple assembly name.</param>
    /// <param name="version">Assembly version, e.g. <c>1.2.0</c>.</param>
    /// <param name="shared">Whether it carries the shared mark its author would give it.</param>
    internal static byte[] Build(string name, string version, bool shared = false)
    {
        // Four parts, as a compiler writes them: a missing one would be stored as 65535.
        Version parsed = Version.Parse(version);
        Version full = new(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0), Math.Max(parsed.Revision, 0));
        PersistedAssemblyBuilder builder = new(new AssemblyName(name) { Version = full }, typeof(object).Assembly);
        if (shared)
        {
            ConstructorInfo metadata = typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!;
            builder.SetCustomAttribute(new CustomAttributeBuilder(metadata, [AppPackageLibrary.SharedMetadataKey, "true"]));
        }

        ModuleBuilder module = builder.DefineDynamicModule(name);
        module.DefineType($"{name}.Marker", TypeAttributes.Public | TypeAttributes.Class).CreateType();
        using MemoryStream stream = new();
        builder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Gets the <c>libraries</c> entry of a package description for an assembly.</summary>
    /// <param name="bytes">The assembly.</param>
    internal static object Entry(byte[] bytes)
    {
        AppPackageLibrary library = AppPackageLibrary.Describe("test.dll", bytes);
        return new { name = library.Name, version = library.Version.ToString(), sha256 = library.Sha256, shared = library.Shared };
    }
}
