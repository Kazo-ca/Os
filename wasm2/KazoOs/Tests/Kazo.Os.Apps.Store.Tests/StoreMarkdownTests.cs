using Xunit;

namespace Kazo.Os.Apps.Store.Tests;

public sealed class StoreMarkdownTests
{
    [Fact]
    public void Blocks_are_headings_paragraphs_lists_and_code()
    {
        IReadOnlyList<StoreMarkdownBlock> blocks = StoreMarkdown.Parse(
            "# Notes\n\nA quick\nnote app.\n\n- one\n- two\n\n1. first\n2. second\n\n```\n<b>raw</b>\n```");

        Assert.Equal(
            [StoreMarkdownBlockKind.Heading, StoreMarkdownBlockKind.Paragraph, StoreMarkdownBlockKind.BulletList,
             StoreMarkdownBlockKind.NumberedList, StoreMarkdownBlockKind.Code],
            blocks.Select(block => block.Kind));
        Assert.Equal("A quick note app.", Assert.Single(blocks[1].Items[0]).Text);
        Assert.Equal(2, blocks[2].Items.Count);
        Assert.Equal("<b>raw</b>", blocks[4].Code);
    }

    [Fact]
    public void Inline_marks_are_read()
    {
        IReadOnlyList<StoreMarkdownSpan> spans = StoreMarkdown.ParseInline("a **b** *c* `d`");

        Assert.Equal(
            [StoreMarkdownSpanKind.Text, StoreMarkdownSpanKind.Strong, StoreMarkdownSpanKind.Text,
             StoreMarkdownSpanKind.Emphasis, StoreMarkdownSpanKind.Text, StoreMarkdownSpanKind.Code],
            spans.Select(span => span.Kind));
    }

    [Fact]
    public void Only_https_links_are_kept()
    {
        IReadOnlyList<StoreMarkdownSpan> spans = StoreMarkdown.ParseInline(
            "[site](https://example.org/a) [bad](javascript:alert(1)) [plain](http://example.org)");

        StoreMarkdownSpan link = Assert.Single(spans, span => span.Kind == StoreMarkdownSpanKind.Link);
        Assert.Equal("https://example.org/a", link.Href);
        Assert.DoesNotContain(spans, span => span.Href?.StartsWith("javascript", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Raw_html_stays_text()
    {
        StoreMarkdownSpan span = Assert.Single(StoreMarkdown.ParseInline("<script>x</script>"));

        Assert.Equal(StoreMarkdownSpanKind.Text, span.Kind);
        Assert.Equal("<script>x</script>", span.Text);
    }

    [Fact]
    public void A_line_that_goes_on_after_a_list_item_stays_in_that_item()
    {
        IReadOnlyList<StoreMarkdownBlock> blocks = StoreMarkdown.Parse(
            "- Headers,\n  footers, page numbers\n- Tables\nwith merged cells\n\nAfter.");

        Assert.Equal([StoreMarkdownBlockKind.BulletList, StoreMarkdownBlockKind.Paragraph], blocks.Select(block => block.Kind));
        Assert.Equal(
            ["Headers, footers, page numbers", "Tables with merged cells"],
            blocks[0].Items.Select(item => string.Concat(item.Select(span => span.Text))));
    }

    [Fact]
    public void A_numbered_item_keeps_its_continuation_and_a_heading_ends_the_list()
    {
        IReadOnlyList<StoreMarkdownBlock> blocks = StoreMarkdown.Parse("1. First\n   step\n2. Second\n## Next");

        Assert.Equal([StoreMarkdownBlockKind.NumberedList, StoreMarkdownBlockKind.Heading], blocks.Select(block => block.Kind));
        Assert.Equal("First step", Assert.Single(blocks[0].Items[0]).Text);
    }

    [Theory]
    [InlineData("# Kazo Word\n\nA word processor.", "\nA word processor.")]
    [InlineData("\n\n## **Kazo word**\nText", "Text")]
    public void A_first_heading_that_repeats_the_apps_name_is_dropped(string markdown, string expected)
    {
        Assert.Equal(expected, StoreMarkdown.WithoutRepeatedTitle(markdown, "Kazo Word"));
    }

    [Theory]
    [InlineData("# Kazo Word for teams\nText")]
    [InlineData("Kazo Word\nText")]
    [InlineData("Intro\n# Kazo Word")]
    public void Any_other_description_is_kept_as_it_is(string markdown)
    {
        Assert.Equal(markdown, StoreMarkdown.WithoutRepeatedTitle(markdown, "Kazo Word"));
    }
}
