using KittyClaw.Web.Markdown;

namespace KittyClaw.Core.Tests;

public class TicketCoverImageTests
{
    [Theory]
    [InlineData("Text ![Screenshot](/uploads/screen.png) more text", "/uploads/screen.png")]
    [InlineData("![Cover][shot]\n\n[shot]: https://example.com/cover.png", "https://example.com/cover.png")]
    [InlineData("![First](https://example.com/one.png) ![Second](/two.png)", "https://example.com/one.png")]
    [InlineData("`![Code](/code.png)` ![Real](/real.png)", "/real.png")]
    [InlineData("```md\n![Code](/code.png)\n```", null)]
    [InlineData("![Unsafe](javascript:alert) ![Safe](/safe.png)", "/safe.png")]
    [InlineData("![Relative](images/cover.png)", "images/cover.png")]
    [InlineData("![File](file:///tmp/cover.png)", null)]
    [InlineData("![Protocol relative](//example.com/cover.png)", null)]
    [InlineData("A description without images", null)]
    [InlineData("", null)]
    public void FindsFirstSafeMarkdownImage(string description, string? expected)
        => Assert.Equal(expected, TicketCoverImage.Find(description));
}
