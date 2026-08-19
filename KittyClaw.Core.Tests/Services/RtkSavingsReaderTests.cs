using KittyClaw.Core.Services;

namespace KittyClaw.Core.Tests.Services;

public sealed class RtkSavingsReaderTests
{
    [Fact]
    public void ParseDaily_ReadsValidRowsAndIgnoresEmptyOrInvalidDays()
    {
        const string json = """
            {
              "summary": { "total_saved": 125 },
              "daily": [
                { "date": "2026-08-18", "commands": 2, "input_tokens": 200, "saved_tokens": 75 },
                { "date": "2026-08-19", "commands": 1, "input_tokens": 100, "saved_tokens": 50 },
                { "date": "2026-08-20", "commands": 1, "input_tokens": 10, "saved_tokens": 0 },
                { "date": "invalid", "commands": 1, "input_tokens": 10, "saved_tokens": 10 }
              ]
            }
            """;

        var rows = RtkSavingsReader.ParseDaily(json);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new DateOnly(2026, 8, 18), rows[0].Day);
        Assert.Equal(75, rows[0].SavedTokens);
        Assert.Equal(50, rows[1].SavedTokens);
    }
}
