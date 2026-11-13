
namespace Kazo.Os.AppSdk.Craft.Tests;

/// <summary>The chords the iframe gives back to Kazo OS or to the window.</summary>
public sealed class ReservedKeysTests
{
    [Fact]
    public void Every_window_manager_shortcut_of_both_key_sets_is_reserved_for_the_OS()
    {
        Assert.Equal(ReservedKeys.OsChords.Count, ReservedKeys.Os.Count);
        Assert.Equal(ReservedKeys.OsChords.Count + 1, ReservedKeys.All.Count);
        Assert.All(ReservedKeys.Os, key => Assert.Equal(ReservedKeyTarget.Os, key.Target));
        Assert.All(ReservedKeys.All.Take(ReservedKeys.OsChords.Count), key => Assert.Equal(ReservedKeyTarget.Os, key.Target));
        Assert.Contains(ReservedKeys.All, key => key == new ReservedKey("F4", null, false, true, false, false, ReservedKeyTarget.Os));
        Assert.Contains(ReservedKeys.All, key => key == new ReservedKey("q", "KeyQ", false, true, false, false, ReservedKeyTarget.Os));
    }

    [Fact]
    public void Ctrl_N_is_the_window_s_own_new_window_chord()
    {
        Assert.Equal(new ReservedKey("n", "KeyN", true, false, false, false, ReservedKeyTarget.App), ReservedKeys.NewWindow);
        Assert.Same(ReservedKeys.NewWindow, ReservedKeys.All[^1]);
    }

    [Theory]
    [InlineData("Alt+Tab", "Tab", null, false, true, false, false)]
    [InlineData("Alt+Shift+Backquote", null, "Backquote", false, true, true, false)]
    [InlineData("Alt+Space", " ", null, false, true, false, false)]
    [InlineData("Super+Left", "ArrowLeft", null, false, false, false, true)]
    [InlineData("Ctrl+Alt+Shift+Right", "ArrowRight", null, true, true, true, false)]
    [InlineData("Ctrl+1", "1", "Digit1", true, false, false, false)]
    public void A_chord_in_the_OS_syntax_is_read_as_the_browser_names_its_keys(
        string chord, string? key, string? code, bool ctrl, bool alt, bool shift, bool meta)
    {
        Assert.Equal(new ReservedKey(key, code, ctrl, alt, shift, meta, ReservedKeyTarget.Os), ReservedKeys.Parse(chord, ReservedKeyTarget.Os));
    }

    [Theory]
    [InlineData("Hyper+F4")]
    [InlineData("+")]
    public void A_chord_with_an_unknown_modifier_or_no_key_is_refused(string chord)
    {
        Assert.Throws<FormatException>(() => ReservedKeys.Parse(chord, ReservedKeyTarget.Os));
    }

    [Fact]
    public void The_message_for_the_proxy_names_its_target()
    {
        var message = Assert.IsType<Dictionary<string, object?>>(ReservedKeys.NewWindow.ToMessage());

        Assert.Equal("app", message["target"]);
        Assert.Equal("n", message["key"]);
        Assert.Equal(true, message["ctrl"]);
    }
}
