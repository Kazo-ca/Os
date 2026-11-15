namespace Kazo.Os.AppSdk.Craft.Tests;

/// <summary>The Craft app's file picker, read from the message of <c>craft-host.js</c>.</summary>
public sealed class PickRequestTests
{
    // WordCraft 0.18.1's filters, seen in its debug host.
    private const string Pictures = ".png,.jpg,.jpeg,.gif,.webp,.bmp,.wmf,.emf";
    private const string Fonts = ".ttf,.otf,.ttc,.otc,.woff,.woff2";
    private static readonly string[] PictureExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".wmf", ".emf", ".svg"];

    [Fact]
    public void Extensions_are_lowercase_with_their_dot_and_without_duplicates()
    {
        Assert.Equal([".png", ".jpg"], new PickRequest(1, " .PNG, .jpg,.png,image/*", false).Extensions);
    }

    [Fact]
    public void Accepts_only_is_true_when_every_extension_is_in_the_set()
    {
        Assert.True(new PickRequest(1, Pictures, false).AcceptsOnly(PictureExtensions));
        Assert.False(new PickRequest(1, Pictures + ",.pdf", false).AcceptsOnly(PictureExtensions));
    }

    [Theory]
    [InlineData("")]
    [InlineData("image/*")]
    public void A_picker_for_any_file_accepts_more_than_any_set(string accept)
    {
        Assert.False(new PickRequest(1, accept, false).AcceptsOnly(PictureExtensions));
        Assert.False(new PickRequest(1, accept, false).AcceptsAny(PictureExtensions));
    }

    [Fact]
    public void Accepts_any_ignores_case()
    {
        Assert.True(new PickRequest(1, Fonts, true).AcceptsAny([".TTF"]));
        Assert.False(new PickRequest(1, Fonts, true).AcceptsAny([".docx"]));
    }

    [Fact]
    public void The_message_of_craft_host_is_read()
    {
        PickRequest? request = PickRequest.FromJson("""{"id":4,"accept":".ttf,.otf","multiple":true}""");

        Assert.Equal(new PickRequest(4, ".ttf,.otf", true), request);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"accept":".png"}""")]
    [InlineData("""{"id":1.5}""")]
    [InlineData("""{"id":"4"}""")]
    [InlineData("[1]")]
    [InlineData("4")]
    public void A_message_that_is_not_a_pick_gives_nothing(string? json)
    {
        Assert.Null(PickRequest.FromJson(json));
    }
}
