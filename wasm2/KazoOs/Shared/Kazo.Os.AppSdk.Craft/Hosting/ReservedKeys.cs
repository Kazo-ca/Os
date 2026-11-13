using System.Globalization;

namespace Kazo.Os.AppSdk.Craft;

/// <summary>Who acts on a reserved chord typed inside a Craft app.</summary>
public enum ReservedKeyTarget
{
    /// <summary>The Kazo OS window manager: <c>craft-host.js</c> replays the key on the OS page.</summary>
    Os,

    /// <summary>The app's window itself (<see cref="CraftFrame.KeyPressed"/>).</summary>
    App,
}

/// <summary>
/// A key combination the iframe gives back instead of the Craft app (ADR 0247). It is matched as the OS matches its own chords:
/// modifiers exactly, then <see cref="Key"/> ignoring case, else the physical <see cref="Code"/>.
/// </summary>
/// <param name="Key">The key's value (<c>F4</c>, <c>Tab</c>, <c>n</c>), or <see langword="null"/> to match by code only.</param>
/// <param name="Code">The physical key (<c>KeyN</c>, <c>Backquote</c>), or <see langword="null"/>.</param>
/// <param name="Ctrl">Control is held.</param>
/// <param name="Alt">Alt is held.</param>
/// <param name="Shift">Shift is held.</param>
/// <param name="Meta">Super (Windows, Command) is held.</param>
/// <param name="Target">Who acts on it.</param>
public sealed record ReservedKey(string? Key, string? Code, bool Ctrl, bool Alt, bool Shift, bool Meta, ReservedKeyTarget Target)
{
    /// <summary>The form <c>craft-host.js</c> sends to the proxy with <c>init</c>.</summary>
    /// <returns>An object serialized to <c>{ key, code, ctrl, alt, shift, meta, target }</c>.</returns>
    public object ToMessage() => new Dictionary<string, object?>
    {
        ["key"] = Key,
        ["code"] = Code,
        ["ctrl"] = Ctrl,
        ["alt"] = Alt,
        ["shift"] = Shift,
        ["meta"] = Meta,
        ["target"] = Target == ReservedKeyTarget.Os ? "os" : "app",
    };
}

/// <summary>
/// The chords a Craft app window reserves: the Kazo OS window manager's shortcuts, which an iframe
/// would otherwise keep (<c>WindowKeyBindingsSettingsDocuments</c>, ADR 0095), and the chords the
/// window acts on itself (<c>Ctrl+N</c>: a new window of the app, not a second document in this one).
/// An app passes <see cref="All"/>, or its own list built from <see cref="Os"/> and <see cref="Parse"/>,
/// in <see cref="CraftFrameOptions.ReservedKeys"/>.
/// </summary>
/// <remarks>
/// The OS lets each user change its shortcuts in <c>~/.config/kazoos/keybindings.config</c>; an app
/// cannot read that file through the SDK, so the defaults of both key sets (<c>[linux]</c> and
/// <c>[fallback]</c>) are reserved. A chord the user moved elsewhere is then simply typed in
/// The Craft app's place by the OS, which ignores it.
/// </remarks>
public static class ReservedKeys
{
    // Named keys of the OS's chord syntax, as KeyboardEvent.key gives them. Declared first: the
    // static fields below are built from it, in declaration order.
    private static readonly Dictionary<string, string> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = " ",
        ["Left"] = "ArrowLeft",
        ["Right"] = "ArrowRight",
        ["Up"] = "ArrowUp",
        ["Down"] = "ArrowDown",
        ["Tab"] = "Tab",
        ["Escape"] = "Escape",
        ["Enter"] = "Enter",
    };

    /// <summary>The window manager's default chords, both key sets (Kazo OS 3.3.0).</summary>
    public static IReadOnlyList<string> OsChords { get; } =
    [
        "Alt+Tab", "Alt+Backquote", "Alt+Shift+Tab", "Alt+Shift+Backquote",
        "Alt+F4", "Alt+Q", "Alt+Space", "Alt+F3", "Alt+F7", "Alt+F8", "Alt+F9", "Alt+F10", "Alt+F11",
        "Super+Up", "Super+Down", "Super+Left", "Super+Right", "Alt+Shift+Left", "Alt+Shift+Right",
        "Super+Shift+Left", "Super+Shift+Right", "Ctrl+Alt+Shift+Left", "Ctrl+Alt+Shift+Right",
    ];

    /// <summary>The window manager's chords, parsed, all targeting <see cref="ReservedKeyTarget.Os"/>.</summary>
    public static IReadOnlyList<ReservedKey> Os { get; } = [.. OsChords.Select(chord => Parse(chord, ReservedKeyTarget.Os))];

    /// <summary>The chord that opens a new window of the app (<c>Ctrl+N</c>).</summary>
    public static ReservedKey NewWindow { get; } = Parse("Ctrl+N", ReservedKeyTarget.App);

    /// <summary>Every reserved chord, the OS's first.</summary>
    public static IReadOnlyList<ReservedKey> All { get; } = [.. Os, NewWindow];

    /// <summary>Parses a chord in the OS's syntax (<c>Ctrl+Alt+Shift+Left</c>, <c>Alt+Backquote</c>).</summary>
    /// <param name="chord">The chord.</param>
    /// <param name="target">Who acts on it.</param>
    /// <returns>The chord.</returns>
    /// <exception cref="FormatException">The chord has no key, or an unknown modifier.</exception>
    public static ReservedKey Parse(string chord, ReservedKeyTarget target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chord);
        string[] parts = chord.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw new FormatException($"'{chord}' names no key.");
        }

        bool ctrl = false, alt = false, shift = false, meta = false;
        foreach (string modifier in parts[..^1])
        {
            switch (modifier.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; break;
                case "alt": alt = true; break;
                case "shift": shift = true; break;
                case "super" or "meta" or "win" or "cmd": meta = true; break;
                default: throw new FormatException($"'{modifier}' in '{chord}' is not a modifier.");
            }
        }

        string last = parts[^1];
        (string? key, string? code) = last switch
        {
            _ when NamedKeys.TryGetValue(last, out string? named) => (named, null),
            _ when last.Equals("Backquote", StringComparison.OrdinalIgnoreCase) => ((string?)null, "Backquote"),
            _ when last.Length == 1 && char.IsLetter(last[0]) =>
                (last.ToLowerInvariant(), "Key" + char.ToUpperInvariant(last[0]).ToString(CultureInfo.InvariantCulture)),
            _ when last.Length == 1 && char.IsDigit(last[0]) => (last, "Digit" + last),
            _ => (last, null),
        };
        return new ReservedKey(key, code, ctrl, alt, shift, meta, target);
    }
}
