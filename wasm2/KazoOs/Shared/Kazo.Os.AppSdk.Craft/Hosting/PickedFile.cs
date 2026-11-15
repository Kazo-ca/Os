namespace Kazo.Os.AppSdk.Craft;

/// <summary>One file handed to the Craft app's file picker.</summary>
/// <param name="Name">The file name, with its extension.</param>
/// <param name="Type">The media type, empty when unknown.</param>
/// <param name="Bytes">The content.</param>
public sealed record PickedFile(string Name, string Type, byte[] Bytes);
