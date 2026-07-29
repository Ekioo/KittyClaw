namespace KittyClaw.Core.Tests.Automation;

public class CreateTicketPlaceholderTests
{
    private static readonly DateTime Now = new(2026, 7, 29, 14, 2, 30);

    [Fact]
    public void Resolves_now_to_date_and_time()
    {
        var result = ActionExecutor.ResolveCreateTicketPlaceholders("Revue du board KittyClaw {now}", Now);
        Assert.Equal("Revue du board KittyClaw 2026-07-29 14:02", result);
    }

    [Fact]
    public void Resolves_all_placeholders()
    {
        var result = ActionExecutor.ResolveCreateTicketPlaceholders(
            "now={now} date={date} time={time} monday={monday} first={firstOfMonth}", Now);
        Assert.Equal("now=2026-07-29 14:02 date=2026-07-29 time=14:02 monday=2026-07-27 first=2026-07-01", result);
    }

    [Fact]
    public void Monday_resolves_to_same_day_when_now_is_monday()
    {
        var monday = new DateTime(2026, 7, 27, 9, 0, 0);
        var result = ActionExecutor.ResolveCreateTicketPlaceholders("{monday}", monday);
        Assert.Equal("2026-07-27", result);
    }

    [Fact]
    public void Leaves_unknown_placeholders_and_empty_string_alone()
    {
        Assert.Equal("{other}", ActionExecutor.ResolveCreateTicketPlaceholders("{other}", Now));
        Assert.Equal("", ActionExecutor.ResolveCreateTicketPlaceholders("", Now));
    }
}
