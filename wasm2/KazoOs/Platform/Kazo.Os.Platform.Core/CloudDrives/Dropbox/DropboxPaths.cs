using Kazo.Os.Simulation.Abstractions.FileSystem;

namespace Kazo.Os.Platform.Core.CloudDrives.Dropbox;

/// <summary>Dropbox paths: <c>""</c> for the root of what the account reaches, otherwise <c>/a/b</c>.</summary>
internal static class DropboxPaths
{
    /// <summary>Normalizes a root chosen by the user; <see langword="null"/> for the whole storage.</summary>
    public static string? NormalizeRoot(string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        string trimmed = root.Trim().Trim('/');
        if (trimmed.Length == 0)
        {
            return null;
        }

        string[] segments = trimmed.Split('/');
        foreach (string segment in segments)
        {
            if (!IsValidSegment(segment))
            {
                throw new ArgumentException($"'{root}' is not a Dropbox folder path.", nameof(root));
            }
        }

        return "/" + string.Join('/', segments);
    }

    /// <summary>The Dropbox path of <paramref name="segments"/> under <paramref name="root"/>.</summary>
    /// <exception cref="HostFolderException"><see cref="HostFolderErrorKind.InvalidName"/> for a segment Dropbox cannot hold.</exception>
    public static string Combine(string? root, IReadOnlyList<string> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        string path = root ?? string.Empty;
        foreach (string segment in segments)
        {
            if (!IsValidSegment(segment))
            {
                throw new HostFolderException(HostFolderErrorKind.InvalidName, $"'{segment}' is not a valid name on Dropbox.");
            }

            path += "/" + segment;
        }

        return path;
    }

    private static bool IsValidSegment(string segment) =>
        segment.Length > 0 && segment is not "." and not ".." && !segment.Contains('/') && !segment.Contains('\\');
}
