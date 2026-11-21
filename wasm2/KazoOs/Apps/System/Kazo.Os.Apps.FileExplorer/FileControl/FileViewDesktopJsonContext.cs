using Kazo.Os.AppSdk;
using System.Text.Json.Serialization;

namespace Kazo.Os.Apps.FileExplorer.FileControl;

/// <summary>Source-generated serializer for the icon keys of a desktop drag (<see cref="FileViewDesktop"/>, ADR 0160 §3).</summary>
[JsonSerializable(typeof(string[]))]
internal sealed partial class FileViewDesktopJsonContext : JsonSerializerContext
{
}
