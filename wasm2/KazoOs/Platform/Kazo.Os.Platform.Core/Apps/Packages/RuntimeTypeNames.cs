using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace Kazo.Os.Platform.Core.Apps.Packages;

/// <summary>
/// One text form for a type in a method signature, written the same way from an assembly's
/// metadata and from a loaded <see cref="Type"/>, so the install check can compare a member
/// reference with the members the trimmed runtime kept (ADR 0242).
/// </summary>
/// <remarks>
/// The form is <c>Namespace.Outer+Inner</c>, generic instances <c>System.ReadOnlySpan`1&lt;System.Char&gt;</c>,
/// a type's generic parameter <c>!0</c>, a method's <c>!!0</c>, arrays <c>[]</c> or <c>[,]</c>, by-ref
/// <c>&amp;</c>, pointers <c>*</c>. Custom modifiers (<c>in</c>, <c>volatile</c>) are ignored: reflection
/// does not show them in <see cref="Type"/> either, and an overload cannot differ by them alone in C#.
/// </remarks>
internal static class RuntimeTypeNames
{
    /// <summary>The name of a function pointer type, the same on both sides (its signature is not compared).</summary>
    private const string FunctionPointer = "fnptr";

    /// <summary>Gets the text form of a loaded type.</summary>
    public static string Of(Type type)
    {
        if (type.IsByRef)
        {
            return Of(type.GetElementType()!) + "&";
        }

        if (type.IsPointer)
        {
            return Of(type.GetElementType()!) + "*";
        }

        if (type.IsArray)
        {
            return Of(type.GetElementType()!) + (type.IsSZArray ? "[]" : $"[{new string(',', type.GetArrayRank() - 1)}]");
        }

        if (type.IsFunctionPointer)
        {
            return FunctionPointer;
        }

        if (type.IsGenericParameter)
        {
            return (type.DeclaringMethod is null ? "!" : "!!") + type.GenericParameterPosition;
        }

        // A generic definition shows here when a member names its own type, e.g. the parameter of
        // Span<T>.CopyTo(Span<T>): reflection gives Span`1 itself, metadata Span`1<!0>.
        if (type.IsGenericType)
        {
            return DefinitionName(type.GetGenericTypeDefinition())
                + "<" + string.Join(",", type.GetGenericArguments().Select(Of)) + ">";
        }

        return DefinitionName(type);
    }

    /// <summary>Gets the text form of a parameter list: <c>(type,type)</c>.</summary>
    public static string Parameters(IEnumerable<string> parameterTypes) => "(" + string.Join(",", parameterTypes) + ")";

    private static string DefinitionName(Type type) =>
        type.IsNested && type.DeclaringType is { } outer
            ? DefinitionName(outer) + "+" + type.Name
            : string.IsNullOrEmpty(type.Namespace) ? type.Name : type.Namespace + "." + type.Name;

    /// <summary>Decodes the types of a metadata signature into the same text form as <see cref="Of"/>.</summary>
    public sealed class MetadataProvider : ISignatureTypeProvider<string, object?>
    {
        /// <summary>Gets the shared instance (it keeps no state).</summary>
        public static MetadataProvider Instance { get; } = new();

        private MetadataProvider()
        {
        }

        /// <inheritdoc />
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode switch
        {
            PrimitiveTypeCode.IntPtr => "IntPtr",
            PrimitiveTypeCode.UIntPtr => "UIntPtr",
            _ => typeCode.ToString(),
        };

        /// <inheritdoc />
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            string name = reader.GetString(type.Name);
            TypeDefinitionHandle outer = type.GetDeclaringType();
            if (!outer.IsNil)
            {
                return GetTypeFromDefinition(reader, outer, rawTypeKind) + "+" + name;
            }

            string ns = reader.GetString(type.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        /// <inheritdoc />
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            TypeReference type = reader.GetTypeReference(handle);
            string name = reader.GetString(type.Name);
            if (type.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                return GetTypeFromReference(reader, (TypeReferenceHandle)type.ResolutionScope, rawTypeKind) + "+" + name;
            }

            string ns = reader.GetString(type.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        /// <inheritdoc />
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        /// <inheritdoc />
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            genericType + "<" + string.Join(",", typeArguments) + ">";

        /// <inheritdoc />
        public string GetSZArrayType(string elementType) => elementType + "[]";

        /// <inheritdoc />
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";

        /// <inheritdoc />
        public string GetByReferenceType(string elementType) => elementType + "&";

        /// <inheritdoc />
        public string GetPointerType(string elementType) => elementType + "*";

        /// <inheritdoc />
        public string GetPinnedType(string elementType) => elementType;

        /// <inheritdoc />
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        /// <inheritdoc />
        public string GetFunctionPointerType(MethodSignature<string> signature) => FunctionPointer;

        /// <inheritdoc />
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;

        /// <inheritdoc />
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    }
}
