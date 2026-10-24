using KittyClaw.Web.Services;

namespace KittyClaw.Core.Tests.Web;

public class ColumnDisplayNameTests
{
    [Theory]
    [InlineData("column-6")]
    [InlineData("column:6")]
    public void ResolvesHistoricalRunIdentitiesToCurrentColumnName(string identity)
    {
        BoardColumn[] columns = [new() { Id = 6, Name = "Review", PipelineId = 2 }];
        Assert.Equal("Review", ColumnDisplayName.Resolve(identity, columns, "Column"));
        columns[0].Name = "Validation";
        Assert.Equal("Validation", ColumnDisplayName.Resolve(identity, columns, "Column"));
    }

    [Fact]
    public void DeletedColumnDoesNotExposeInternalIdentity()
        => Assert.Equal("Column", ColumnDisplayName.Resolve("column-6", [], "Column"));

    [Theory]
    [InlineData("dashboard")]
    [InlineData("chat:project:agent")]
    [InlineData("column-specialist")]
    public void PreservesOtherAgentNamesAndGroups(string value)
        => Assert.Equal(value, ColumnDisplayName.Resolve(value, [], "Column"));
}
