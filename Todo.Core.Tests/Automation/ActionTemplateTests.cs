namespace Todo.Core.Tests.Automation;

public class ActionTemplateTests
{
    [Fact]
    public void RenderCommentTemplate_substitutes_all_placeholders()
    {
        var result = ConditionEvaluators.RenderCommentTemplate(
            "Ticket #{ticketId} [{ticketTitle}] assigned to {assignee}",
            42, "Fix drawer", "programmer");
        Assert.Equal("Ticket #42 [Fix drawer] assigned to programmer", result);
    }

    [Fact]
    public void RenderCommentTemplate_replaces_missing_values_with_empty()
    {
        var result = ConditionEvaluators.RenderCommentTemplate(
            "ID:{ticketId} T:{ticketTitle} A:{assignee}", null, null, null);
        Assert.Equal("ID: T: A:", result);
    }

    [Fact]
    public void RenderCommentTemplate_leaves_other_braces_alone()
    {
        var result = ConditionEvaluators.RenderCommentTemplate(
            "{other} {ticketId}", 1, null, null);
        Assert.Equal("{other} 1", result);
    }
}
