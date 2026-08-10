using AuditionModStudio.Archives;

namespace Archives.Tests;

public sealed class AcvTool5OutputParserTests
{
    [Fact]
    public void Select_prompt_without_newline_is_detected()
    {
        var parser = new AcvTool5OutputParser();

        var events = parser.Feed("Select:".AsSpan());

        Assert.Contains(events, item => item.Kind == AcvTool5ParsedEventKind.CountrySelectionRequested);
    }

    [Fact]
    public void Select_prompt_split_across_chunks_is_detected_once()
    {
        var parser = new AcvTool5OutputParser();

        var first = parser.Feed("Sel".AsSpan());
        var second = parser.Feed("ect:".AsSpan());
        var third = parser.Feed(" Select:".AsSpan());

        Assert.DoesNotContain(first, item => item.Kind == AcvTool5ParsedEventKind.CountrySelectionRequested);
        Assert.Single(second, item => item.Kind == AcvTool5ParsedEventKind.CountrySelectionRequested);
        Assert.DoesNotContain(third, item => item.Kind == AcvTool5ParsedEventKind.CountrySelectionRequested);
    }

    [Fact]
    public void Protocol_tokens_split_across_many_small_chunks_are_detected()
    {
        var parser = new AcvTool5OutputParser();
        var events = new List<AcvTool5ParsedEvent>();

        foreach (var character in "Keydat file not found Select:")
        {
            events.AddRange(parser.Feed(new[] { character }));
        }

        Assert.Single(events, item => item.Kind == AcvTool5ParsedEventKind.KeydatMissing);
        Assert.Single(events, item => item.Kind == AcvTool5ParsedEventKind.CountrySelectionRequested);
    }

    [Fact]
    public void Writing_progress_line_is_parsed()
    {
        var parser = new AcvTool5OutputParser();

        var parsed = Assert.Single(
            parser.Feed("writing : 015\\texture\\file.dds\r\n".AsSpan()),
            item => item.Kind == AcvTool5ParsedEventKind.ExtractItem);

        Assert.Equal("015\\texture\\file.dds", parsed.Value);
    }

    [Fact]
    public void Packing_progress_line_is_parsed()
    {
        var parser = new AcvTool5OutputParser();

        var parsed = Assert.Single(
            parser.Feed("Packing: 015\\texture\\file.dds\n".AsSpan()),
            item => item.Kind == AcvTool5ParsedEventKind.PackItem);

        Assert.Equal("015\\texture\\file.dds", parsed.Value);
    }

    [Fact]
    public void Unknown_output_does_not_crash_or_create_events()
    {
        var parser = new AcvTool5OutputParser();

        var events = parser.Feed("ordinary output\n".AsSpan());

        Assert.Empty(events);
    }

    [Fact]
    public void Unterminated_output_buffer_is_bounded()
    {
        var parser = new AcvTool5OutputParser();

        parser.Feed(new string('x', AcvTool5OutputParser.MaximumBufferedCharacters * 4).AsSpan());

        Assert.InRange(
            parser.BufferedCharacterCount,
            0,
            AcvTool5OutputParser.MaximumBufferedCharacters + "Keydat file not found".Length - 1);
    }
}
