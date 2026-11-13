namespace Kazo.Os.AppSdk.Craft;

/// <summary>One file format a Craft app knows (ADR 0259 §2: the app gives the list).</summary>
/// <param name="Extension">Extension with its dot, lowercase (<c>.docx</c>).</param>
/// <param name="MediaType">Media type written in the manifest and given to the OS dialogs.</param>
/// <param name="IsDefault">
/// The app handles <c>open</c> and <c>edit</c> for this format (its own documents); otherwise only
/// <c>open</c>, for "Open with" (another app stays the default).
/// </param>
/// <param name="IsCopy">
/// The app writes this format as a copy (an export such as PDF or PNG): the document stays bound to
/// its own file.
/// </param>
/// <param name="Opens">The app opens this format (an export-only format does not).</param>
public sealed record CraftDocumentFormat(string Extension, string MediaType, bool IsDefault, bool IsCopy, bool Opens = true);
